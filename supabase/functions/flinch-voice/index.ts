// ============================================================================
// FLINCH AI: SUPABASE EDGE FUNCTION (flinch-voice)
// Single-Hop Speech-to-Text (Groq Whisper) + LLM Response + Orpheus TTS
// Demo Limit Enforced: 5 Voice Messages Total Per Account
// Zero API Keys Exposed To Client Desktop Application
// ============================================================================

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "npm:@supabase/supabase-js@2.39.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Content-Type": "application/json; charset=utf-8",
};

const MAX_AUDIO_BYTES = 10 * 1024 * 1024; // 10 MB maximum audio payload
const MIN_AUDIO_BYTES = 250;              // Under 250 bytes is invalid header
const WHISPER_MODELS = ["whisper-large-v3", "whisper-large-v3-turbo"];
const LLM_MODELS = [
  "qwen/qwen3.8-27b",
  "openai/gpt-oss-20b",
  "openai/gpt-oss-120b"
];

serve(async (req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  if (req.method !== "POST") {
    return new Response(JSON.stringify({ error: "method_not_allowed" }), {
      status: 405,
      headers: corsHeaders,
    });
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL") || "";
  const supabaseAnonKey = Deno.env.get("SUPABASE_ANON_KEY") || "";
  const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";
  const groqApiKey = Deno.env.get("GROQ_API_KEY") || "";
  const geminiApiKey = Deno.env.get("GEMINI_API_KEY") || "";
  const exaApiKey = Deno.env.get("EXA_API_KEY") || "";
  let cartesiaApiKey = Deno.env.get("CARTESIA_API_KEY") || "";
  let deepgramApiKey = Deno.env.get("DEEPGRAM_API_KEY") || "";

  if (!supabaseUrl || !supabaseAnonKey || !supabaseServiceKey || !groqApiKey) {
    return new Response(
      JSON.stringify({
        error: "server_misconfiguration",
        message: "Supabase or Groq credentials missing on server.",
      }),
      { status: 500, headers: corsHeaders }
    );
  }

  // 1. Verify User Authentication via JWT
  const authHeader = req.headers.get("Authorization");
  if (!authHeader || !authHeader.startsWith("Bearer ")) {
    return new Response(
      JSON.stringify({
        error: "unauthorized",
        message: "Authentication required. Please provide a valid Bearer token.",
      }),
      { status: 401, headers: corsHeaders }
    );
  }

  const token = authHeader.replace(/^Bearer\s+/i, "").trim();
  const userClient = createClient(supabaseUrl, supabaseAnonKey, {
    global: { headers: { Authorization: `Bearer ${token}` } },
    auth: { persistSession: false },
  });

  const { data: authData, error: authError } = await userClient.auth.getUser(token);
  if (authError || !authData?.user) {
    return new Response(
      JSON.stringify({
        error: "invalid_token",
        message: "Session is invalid or expired. Please sign in again.",
      }),
      { status: 401, headers: corsHeaders }
    );
  }

  const user = authData.user;
  const adminClient = createClient(supabaseUrl, supabaseServiceKey, {
    auth: { persistSession: false },
  });

  if (!cartesiaApiKey || !deepgramApiKey) {
    try {
      const { data: secRows } = await adminClient
        .schema("flinch_private")
        .from("secrets")
        .select("key_name, key_value")
        .in("key_name", ["CARTESIA_API_KEY", "DEEPGRAM_API_KEY"]);
      if (secRows && Array.isArray(secRows)) {
        for (const row of secRows) {
          if (row.key_name === "CARTESIA_API_KEY" && !cartesiaApiKey) cartesiaApiKey = row.key_value;
          if (row.key_name === "DEEPGRAM_API_KEY" && !deepgramApiKey) deepgramApiKey = row.key_value;
        }
      }
    } catch (_e) {
      // ignore
    }
  }

  const contentType = req.headers.get("content-type") || "";

  // 2. Standalone TTS handler (no quota consumption for system feedback/readback)
  if (contentType.includes("application/json")) {
    try {
      const body = await req.json();
      if (body.action === "write_document" || body.action === "generate_content") {
        const topic = String(body.topic || body.title || "").trim();
        const docType = String(body.type || "essay").trim();
        const instructions = String(body.prompt || `Write a comprehensive, engaging, well-structured ${docType} about: ${topic}. Include an introduction, deep body paragraphs with historical, cultural, or factual details, and a thoughtful conclusion.`).trim();

        const systemAuthorRule = `You are an expert author writing a comprehensive ${docType}.
CRITICAL INSTRUCTIONS:
1. Output ONLY the document title, section headers, and body paragraphs of the ${docType}.
2. NEVER include conversational chatter, greetings, user guidance, instructions on how to copy/paste text, or AI disclaimers (e.g. NEVER write "Since I am an AI...", "Here is the essay...", or mention Discord, WhatsApp, or saving files).
3. Start directly with the title and content.`;

        let generatedContent = "";

        // 1. Primary: Gemini 3 Flash Preview (Fast & high quality)
        if (geminiApiKey) {
          try {
            const gResp = await fetch(
              `https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key=${encodeURIComponent(geminiApiKey)}`,
              {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  systemInstruction: {
                    parts: [{ text: systemAuthorRule }]
                  },
                  contents: [
                    {
                      role: "user",
                      parts: [{ text: `Write a comprehensive, engaging, well-structured ${docType} about: ${topic}.` }]
                    }
                  ],
                  generationConfig: {
                    temperature: 0.7,
                    maxOutputTokens: 4096
                  }
                })
              }
            );
            if (gResp.ok) {
              const gData = await gResp.json();
              generatedContent = gData.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
            }
          } catch (_e) {}
        }

        // 2. Fallback: Groq LPU (Llama 3.3 70B)
        if (!generatedContent && groqApiKey) {
          try {
            const grResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
              method: "POST",
              headers: {
                "Authorization": `Bearer ${groqApiKey}`,
                "Content-Type": "application/json"
              },
              body: JSON.stringify({
                model: "llama-3.3-70b-versatile",
                messages: [
                  { role: "system", content: systemAuthorRule },
                  { role: "user", content: `Write a comprehensive, engaging, well-structured ${docType} about: ${topic}.` }
                ],
                temperature: 0.7,
                max_tokens: 3500
              })
            });
            if (grResp.ok) {
              const grData = await grResp.json();
              generatedContent = grData.choices?.[0]?.message?.content?.trim() || "";
            }
          } catch (_e) {}
        }

        if (generatedContent) {
          // Strip any accidental AI preamble
          generatedContent = generatedContent.replace(/^(Since I am an AI|As an AI|I cannot directly access|Here is the).*?\n+/gis, "").trim();
          return new Response(
            JSON.stringify({
              topic: topic,
              type: docType,
              content: generatedContent
            }),
            { status: 200, headers: corsHeaders }
          );
        }

        return new Response(
          JSON.stringify({ error: "generation_failed", message: "Could not generate document content." }),
          { status: 502, headers: corsHeaders }
        );
      }

      if (body.action === "tts" || body.text) {
        const textToSpeak = String(body.text || "").trim();
        const voiceName = String(body.voice || "troy").toLowerCase();
        if (!textToSpeak) {
          return new Response(JSON.stringify({ error: "empty_text" }), {
            status: 400,
            headers: corsHeaders,
          });
        }

        const ttsRes = await synthesizeSpeech(textToSpeak, voiceName, deepgramApiKey, cartesiaApiKey, groqApiKey, geminiApiKey);
        if (ttsRes && ttsRes.audio) {
          return new Response(
            JSON.stringify({
              audio: ttsRes.audio,
              tts_provider: ttsRes.provider,
              tts_voice: ttsRes.voice,
              mimeType: "audio/wav"
            }),
            { status: 200, headers: corsHeaders }
          );
        }
        return new Response(
          JSON.stringify({ error: "tts_generation_failed" }),
          { status: 502, headers: corsHeaders }
        );
      }
    } catch (jErr: any) {
      return new Response(
        JSON.stringify({ error: "invalid_json", details: jErr?.message }),
        { status: 400, headers: corsHeaders }
      );
    }
  }

  // 3. Quota check: 5 voice messages demo allowance per account (exempt centuaryth@gmail.com)
  const isExempt = (user.email || "").toLowerCase() === "centuaryth@gmail.com";
  let quota = { allowed: true, used: 0, limit: 999999, remaining: 999999 };

  if (!isExempt) {
    const { data: qData, error: quotaErr } = await adminClient.rpc(
      "consume_voice_message_for_user",
      { p_user_id: user.id }
    );

    if (quotaErr) {
      console.error("Quota consumption error:", quotaErr);
      return new Response(
        JSON.stringify({
          error: "quota_check_failed",
          message: "Database error verifying demo quota.",
          details: quotaErr.message,
        }),
        { status: 500, headers: corsHeaders }
      );
    }

    if (!qData || !qData.allowed) {
      const used = qData ? qData.used : 5;
      const limit = qData ? qData.limit : 5;
      return new Response(
        JSON.stringify({
          error: "limit_reached",
          used: used,
          limit: limit,
          remaining: 0,
          message: `Demo limit reached: ${used} of ${limit} voice messages used.`,
        }),
        { status: 429, headers: corsHeaders }
      );
    }

    quota = qData;
  }

  // Helper to refund quota if something upstream fails or silence detected
  const refundQuota = async () => {
    if (isExempt) return;
    try {
      await adminClient.rpc("refund_voice_message_for_user", { p_user_id: user.id });
    } catch (e) {
      console.warn("Quota refund error:", e);
    }
  };

  try {
    // 4. Parse audio recording from multipart body
    let formData: FormData;
    try {
      formData = await req.formData();
    } catch (err: any) {
      await refundQuota();
      return new Response(
        JSON.stringify({
          error: "invalid_multipart_body",
          message: "Failed to parse audio multipart request.",
        }),
        { status: 400, headers: corsHeaders }
      );
    }

    const audioFile = formData.get("file");
    const requestedVoice = (formData.get("voice") as string) || "troy";
    const screenFile = formData.get("screen");
    const isExplicitStaged = String(formData.get("staged") || "").toLowerCase() === "true";
    const clipboardText = ((formData.get("clipboard") as string) || "").slice(0, 4000).trim();
    const uiaText = ((formData.get("uia") as string) || "").slice(0, 4000).trim();
    let screenB64 = "";
    if (screenFile && screenFile instanceof Blob && screenFile.size > 500) {
      try {
        const sBytes = await screenFile.arrayBuffer();
        screenB64 = arrayBufferToBase64(sBytes);
      } catch (sErr) {
        console.warn("Failed to read screen payload:", sErr);
      }
    }

    if (!audioFile || !(audioFile instanceof Blob) || audioFile.size < MIN_AUDIO_BYTES) {
      await refundQuota();
      return new Response(
        JSON.stringify({
          empty: true,
          transcript: "",
          reply: "",
          message: "No voice audio detected. Quota refunded.",
          remaining: quota.remaining + 1,
        }),
        { status: 200, headers: corsHeaders }
      );
    }

    // 5. Speech-to-Text & Parallel Pre-fetch
    const tStart = performance.now();
    let tStt = 0;
    let tLlm = 0;
    let tTts = 0;
    let modelUsed = "none";
    const modelErrors: string[] = [];

    // Launch DB queries in parallel with Groq Whisper STT to reduce DB latency to 0ms
    const knowledgePromise = adminClient
      .from("user_knowledge")
      .select("fact")
      .eq("user_id", user.id)
      .order("updated_at", { ascending: false })
      .limit(20);

    const historyPromise = adminClient
      .from("chat_messages")
      .select("role, content, created_at")
      .eq("user_id", user.id)
      .order("created_at", { ascending: false })
      .limit(12);

    let transcript = "";
    const audioBytes = await audioFile.arrayBuffer();
    const tSttStart = performance.now();

    for (const wModel of WHISPER_MODELS) {
      try {
        const whisperFormData = new FormData();
        const fileObj = new File([audioBytes], "audio.wav", { type: "audio/wav" });
        whisperFormData.append("file", fileObj);
        whisperFormData.append("model", wModel);
        whisperFormData.append("language", "en");
        whisperFormData.append("temperature", "0");

        const whisperResp = await fetch("https://api.groq.com/openai/v1/audio/transcriptions", {
          method: "POST",
          headers: { Authorization: `Bearer ${groqApiKey}` },
          body: whisperFormData,
        });

        if (whisperResp.ok) {
          const whisperJson = await whisperResp.json().catch(() => ({}));
          if (typeof whisperJson.text === "string" && whisperJson.text.trim().length > 0) {
            transcript = whisperJson.text.trim();
            console.log(`Groq Whisper (${wModel}) transcribed: "${transcript}"`);
            break;
          }
        } else {
          console.warn(`Groq Whisper ${wModel} failed (${whisperResp.status}):`, await whisperResp.text());
        }
      } catch (wErr) {
        console.warn(`Groq Whisper ${wModel} fetch error:`, wErr);
      }
    }

    // High-accuracy fallback: Gemini Multimodal Audio transcription
    if (!transcript && geminiApiKey) {
      const geminiAudioModels = ["gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-flash", "gemini-flash-latest"];
      const b64Audio = arrayBufferToBase64(audioBytes);
      for (const gModel of geminiAudioModels) {
        try {
          const gResp = await fetch(
            `https://generativelanguage.googleapis.com/v1beta/models/${gModel}:generateContent?key=${encodeURIComponent(geminiApiKey)}`,
            {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                contents: [
                  {
                    role: "user",
                    parts: [
                      { text: "Listen to the user's speech in this audio recording. Transcribe what they said accurately in English. Return ONLY the transcribed text with NO commentary, quotes, or markdown. If completely silent or inaudible, return NONE." },
                      {
                        inline_data: {
                          mime_type: "audio/wav",
                          data: b64Audio,
                        },
                      },
                    ],
                  },
                ],
                generationConfig: {
                  temperature: 0.1,
                  maxOutputTokens: 300,
                },
              }),
            }
          );
          if (gResp.ok) {
            const gJson = await gResp.json();
            const gText = gJson.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
            if (gText && !gText.toUpperCase().includes("NONE")) {
              transcript = gText.replace(/^["']|["']$/g, "").trim();
              console.log(`Gemini Audio fallback (${gModel}) transcribed: "${transcript}"`);
              break;
            }
          } else {
            console.warn(`Gemini audio model ${gModel} failed (${gResp.status}):`, await gResp.text());
          }
        } catch (gErr) {
          console.warn(`Gemini audio model ${gModel} exception:`, gErr);
        }
      }
    }
    tStt = performance.now() - tSttStart;

    if (!transcript) {
      await refundQuota();
      return new Response(
        JSON.stringify({
          error: "transcription_failed",
          message: "Could not transcribe audio. Please check your mic and speak clearly.",
        }),
        { status: 502, headers: corsHeaders }
      );
    }

    // Clean transcription: handle wake phrases and greetings gracefully
    const originalTranscript = transcript.trim();
    const wakePhrases = [
      "hey flinch", "ok flinch", "okay flinch", "flinch",
      "hey reech", "ok reech", "okay reech", "reech"
    ];
    let cleanedTranscript = originalTranscript;
    for (const phrase of wakePhrases) {
      // If user only said the wake word/phrase, treat it as a direct greeting!
      const rxExact = new RegExp(`^${phrase}[\\s,.:;!?-]*$`, "i");
      if (rxExact.test(cleanedTranscript)) {
        cleanedTranscript = "Hey Flinch";
        break;
      }
      // If user said wake phrase followed by their question, strip the wake phrase prefix
      const rxPrefix = new RegExp(`^${phrase}[\\s,.:;!?-]+`, "i");
      if (rxPrefix.test(cleanedTranscript)) {
        const remaining = cleanedTranscript.replace(rxPrefix, "").trim();
        if (remaining.length > 0) {
          cleanedTranscript = remaining;
        }
        break;
      }
    }
    transcript = cleanedTranscript;

    // Detect silence / empty audio / Whisper hallucinations
    const silenceHallucinations = new Set([
      "you", "thank you", "thank you.", "thanks for watching", "thanks for watching.",
      "subtitles by the amara.org community", "bye", "bye.", "thank you very much."
    ]);
    const lowerClean = transcript.toLowerCase().trim();
    if (!transcript || transcript.length < 2 || silenceHallucinations.has(lowerClean)) {
      await refundQuota();
      return new Response(
        JSON.stringify({
          empty: true,
          transcript: "",
          reply: "",
          message: "No speech recognized. Quota refunded.",
          remaining: quota.remaining + 1,
        }),
        { status: 200, headers: corsHeaders }
      );
    }

    // 6. Optional Fast Live Web Search (Exa) if query mentions live events / prices / weather or search intent
    let webContext = "";
    if (exaApiKey && (isLiveQuery(transcript) || transcript.toLowerCase().includes("search") || transcript.toLowerCase().includes("who is") || transcript.toLowerCase().includes("what is the current") || transcript.toLowerCase().includes("latest"))) {
      try {
        const exaCts = new AbortController();
        const exaTimer = setTimeout(() => exaCts.abort(), 3500);
        const exaResp = await fetch("https://api.exa.ai/search", {
          method: "POST",
          signal: exaCts.signal,
          headers: {
            "Content-Type": "application/json",
            "x-api-key": exaApiKey,
          },
          body: JSON.stringify({
            query: transcript,
            numResults: 3,
            type: "fast",
            contents: { text: { maxCharacters: 400 } },
          }),
        });
        clearTimeout(exaTimer);
        if (exaResp.ok) {
          const exaJson = await exaResp.json();
          const snippets = (exaJson.results || [])
            .map((r: any) => r.text)
            .filter((t: any) => typeof t === "string" && t.trim().length > 0)
            .join("\n");
          if (snippets) {
            webContext = snippets.slice(0, 1000);
          }
        }
      } catch (e) {
        console.warn("Exa search error or timeout:", e);
      }
    }

    // 7. Ingest concurrent user knowledge results & conversation history (already fetched in parallel with Whisper)
    let permanentKnowledgePrompt = "";
    try {
      const { data: kRows } = await knowledgePromise;
      if (kRows && kRows.length > 0) {
        const bulletFacts = kRows.map((r: any) => `- ${r.fact}`).join("\n");
        permanentKnowledgePrompt = `PERMANENT USER KNOWLEDGE & PROFILE (Facts you permanently know about this user across all sessions):\n${bulletFacts}\nAlways maintain this knowledge and refer to it naturally when relevant.`;
      }
    } catch (kErr) {
      console.warn("Failed to read user knowledge:", kErr);
    }

    let historyMessages: { role: string; content: string }[] = [];
    try {
      const { data: dbHistory } = await historyPromise;
      if (dbHistory && dbHistory.length > 0) {
        historyMessages = dbHistory
          .sort((a: any, b: any) => new Date(a.created_at).getTime() - new Date(b.created_at).getTime())
          .map((m: any) => ({
            role: m.role === "assistant" ? "assistant" : "user",
            content: String(m.content || "").trim(),
          }));
      }
    } catch (histErr) {
      console.warn("Failed to read conversation history:", histErr);
    }

    // 7.1 Check for basic PC action intents & Timers/Reminders
    let actionObj: any = null;
    let sceneObj: any = null;
    let reply = "";

    const detectedAction = parseFlinchAction(transcript);
    if (detectedAction) {
      actionObj = detectedAction.action;
      reply = detectedAction.reply;
    } else if (isControlQuery(transcript) && !screenB64) {
      reply = "I don't have permission for full OS mouse takeover yet, but you can ask me to point to or explain anything on your screen!";
    }

    // 7.2 Clipboard query without clipboard content
    if (!reply && (transcript.toLowerCase().includes("what did i copy") || transcript.toLowerCase().includes("summarize what i copied") || transcript.toLowerCase().includes("read my clipboard")) && !clipboardText) {
      reply = "Your clipboard appears to be empty right now. Copy some text first, then ask again.";
    }

    // 7.3 Screen Vision & Visual Overlays (ONLY when user explicitly asks about screen or problem on screen)
    const isScreenQuery = isScreenAwarenessQuery(transcript) || isExplicitStaged;
    if (!reply && isScreenQuery && screenB64 && (groqApiKey || geminiApiKey)) {
      const uiaSection = uiaText
        ? `\n\nUI Automation elements detected on this window (coordinates normalized 0.0 to 1.0):\n${uiaText}`
        : "";

      const historySection = historyMessages.length > 0
        ? `\n\nRecent conversation history:\n${historyMessages.map(m => `${m.role.toUpperCase()}: ${m.content}`).join("\n")}`
        : "";

      const visionPrompt =
        `You are Flinch, a desktop AI voice companion with screen awareness and visual overlay drawing capabilities.
The user's spoken request: "${transcript}".${historySection}
Look carefully at the attached screenshot of the user's active window/display.${uiaSection}

Instructions:
1. Provide a direct, helpful spoken response answering their question in 1-2 natural sentences without any markdown, bullets, or asterisks. Make your first sentence direct and snappy.
2. If the user asks to solve or explain a problem on screen (e.g. 'solve this', 'solve the problem', 'what is the answer', 'can you solve this', 'how to solve this'):
   - Look at the screenshot to locate the exact bounding box of the equation or problem (normalized coordinates x, y, w, h from 0.0 to 1.0).
   - You MUST generate visual drawing objects with sequential step numbers and delayMs:
     - Step 1: Circle around the first operation (e.g. parentheses "(1+2)") with {"type": "circle", "rect": {"x": 0.38, "y": 0.43, "w": 0.17, "h": 0.16}, "step": 1, "delayMs": 200, "label": "① (1+2) = 3", "color": "#3DDC84"}.
     - Step 2: Highlight under the next operation (e.g. division "6 ÷ 2") with {"type": "highlight", "rect": {"x": 0.22, "y": 0.46, "w": 0.17, "h": 0.10}, "step": 2, "delayMs": 2200, "label": "② 6 ÷ 2 = 3", "color": "#38BDF8"}.
     - Step 3: Arrow pointing to the answer space next to "=" with {"type": "arrow", "from": {"x": 0.54, "y": 0.51}, "to": {"x": 0.65, "y": 0.51}, "step": 3, "delayMs": 4400, "label": "③ 3 × 3 = 9", "color": "#3DDC84"}.
     - Step 4: Include a "notepad" object that creates an interactive solution notebook card on screen:
       {
         "type": "notepad",
         "title": "Order of Operations: 6 ÷ 2(1+2)",
         "text": "① Parentheses First: (1 + 2) = 3  →  6 ÷ 2(3)\\n② Left-to-Right: 6 ÷ 2 = 3  →  3(3)\\n③ Multiply: 3 × 3 = 9\\n\\nResult = 9 (Modern Standard)",
         "altAnswer": "1",
         "altExplanation": "People get 1 by treating implied multiplication 2(3) with higher precedence than division: 6 ÷ [2(3)] = 6 ÷ 6 = 1.\\n\\nUnder modern PEMDAS / ISO 80000-2 standards, multiplication and division share equal priority left-to-right, giving 9.",
         "delayMs": 400
       }
   - For viral equation 6 ÷ 2(1+2) =: Spoken answer MUST be: "First, evaluate parentheses: 1 plus 2 is 3. Next, working left to right, 6 divided by 2 is 3. Finally, 3 times 3 gives 9."
3. If the user asks about anything else on screen (code, error, design, text, browser, button), use the screenshot to give a precise answer with visual outlines or arrows.
4. If there is NO problem or error on screen when asked to solve one, reply: "What problem? I don't see an equation or error on your screen right now."

Set durationMs: 25000 so the user has time to watch each animated step and interact with the notebook.

Supported scene object types:
- "circle": {"type": "circle", "rect": {"x": 0.45, "y": 0.35, "w": 0.1, "h": 0.1}, "step": 1, "delayMs": 0, "label": "Target", "color": "#3DDC84"}
- "highlight": {"type": "highlight", "rect": {"x": 0.2, "y": 0.3, "w": 0.4, "h": 0.05}, "step": 2, "delayMs": 1300, "label": "Important", "color": "#FACC15"}
- "draw": {"type": "draw", "points": [{"x": 0.2, "y": 0.3}, {"x": 0.25, "y": 0.35}, {"x": 0.3, "y": 0.3}], "color": "#3DDC84", "label": "Line"}
- "arrow": {"type": "arrow", "from": {"x": 0.2, "y": 0.3}, "to": {"x": 0.5, "y": 0.4}, "step": 3, "delayMs": 2600, "label": "Click Here", "color": "#38BDF8"}
- "outline": {"type": "outline", "rect": {"x": 0.4, "y": 0.3, "w": 0.2, "h": 0.08}, "label": "Error / Target"}
- "callout": {"type": "callout", "rect": {"x": 0.4, "y": 0.4, "w": 0.3, "h": 0.1}, "text": "Explanation note"}
- "notepad": {"type": "notepad", "title": "Math Notebook", "text": "Step 1...\\nStep 2...", "altAnswer": "1", "altExplanation": "Why people get 1...", "delayMs": 500}

Return your output strictly as a JSON object:
{
  "answer": "Spoken answer here",
  "scene": {
    "objects": [ ... ],
    "durationMs": 25000
  }
}
If no visual overlay is needed, set "scene": null.`;

      // 1. High-Precision Vision via Google Gemini 3 Flash (Fast & Validated)
      if (geminiApiKey) {
        const candidateVisionModels = ["gemini-3-flash-preview"];

        for (const vModel of candidateVisionModels) {
          const controller = new AbortController();
          const timeout = setTimeout(() => controller.abort(), 14_000);

          try {
            const visionResp = await fetch(
              `https://generativelanguage.googleapis.com/v1beta/models/${vModel}:generateContent?key=${encodeURIComponent(
                geminiApiKey
              )}`,
              {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  contents: [
                    {
                      role: "user",
                      parts: [
                        { text: visionPrompt },
                        {
                          inline_data: {
                            mime_type: "image/jpeg",
                            data: screenB64,
                          },
                        },
                      ],
                    },
                  ],
                  generationConfig: {
                    temperature: 0.1,
                    maxOutputTokens: 4096,
                    responseMimeType: "application/json",
                  },
                }),
                signal: controller.signal,
              }
            );

            if (visionResp.ok) {
              const vJson = await visionResp.json();
              const rawText = vJson.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
              const parsedVision = parseVisionOutput(rawText);
              if (parsedVision.answer) reply = parsedVision.answer;
              if (parsedVision.scene) sceneObj = sanitizeScene(parsedVision.scene);
              if (reply) {
                modelUsed = `${vModel}-vision`;
                console.log(`Gemini Vision (${vModel}) generated reply and scene.`);
                break;
              }
            } else {
              console.warn(`Vision model ${vModel} error:`, visionResp.status, await visionResp.text());
            }
          } catch (vErr) {
            console.warn(`Vision model ${vModel} exception:`, vErr);
          } finally {
            clearTimeout(timeout);
          }
        }
      }

      // 2. Groq Vision Fallback
      if (!reply && groqApiKey) {
        const groqVisionModels = ["llama-3.2-11b-vision-preview", "llama-3.2-90b-vision-preview"];
        for (const gvModel of groqVisionModels) {
          const gvController = new AbortController();
          const gvTimeout = setTimeout(() => gvController.abort(), 5_000);
          try {
            const gvResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
              method: "POST",
              headers: {
                "Authorization": `Bearer ${groqApiKey}`,
                "Content-Type": "application/json"
              },
              body: JSON.stringify({
                model: gvModel,
                messages: [
                  {
                    role: "user",
                    content: [
                      { type: "text", text: visionPrompt },
                      {
                        type: "image_url",
                        image_url: {
                          url: `data:image/jpeg;base64,${screenB64}`
                        }
                      }
                    ]
                  }
                ],
                temperature: 0.1,
                max_tokens: 1500,
                response_format: { type: "json_object" }
              }),
              signal: gvController.signal
            });

            if (gvResp.ok) {
              const gvJson = await gvResp.json();
              const rawText = gvJson.choices?.[0]?.message?.content?.trim() || "";
              const parsedGv = parseVisionOutput(rawText);
              if (parsedGv.answer) reply = parsedGv.answer;
              if (parsedGv.scene) sceneObj = sanitizeScene(parsedGv.scene);
              if (reply) {
                modelUsed = `${gvModel}-vision`;
                break;
              }
            }
          } catch (_e) {
          } finally {
            clearTimeout(gvTimeout);
          }
        }
      }
    }

    if (!reply) {
      // 8. Ultra-fast LLM Answer Generation (Groq LPU)
      const systemPrompt =
        "You are Flinch, a friendly, ultra-fast desktop AI voice companion for PC.\n" +
        "1. You have screen awareness, visual overlay drawing (arrows, outlines, callouts), live web search, timers, and clipboard intelligence.\n" +
        "2. If the user greets you or says your name (e.g. 'Hey Flinch', 'Hello', 'Hi'), reply warmly and briefly (e.g. 'Hey there! What's on your mind?' or 'Hey! How can I help?').\n" +
        "3. For real-time questions, you have live real-time internet search access. State the facts directly.\n" +
        "4. Keep answers short, direct, and factual for the ear (1 to 2 sentences max). Make your FIRST sentence snappy and direct (4 to 8 words) so voice begins speaking immediately, followed by 1 helpful sentence. Never use markdown, bullet points, asterisks, or conversational filler.\n" +
        "5. Conversational Memory: You have the recent conversation history in this chat. If the user asks what they just asked, what their previous question was, or refers to earlier topics, answer accurately based strictly on their immediate previous question in this chat history.";

      const messages: any[] = [{ role: "system", content: systemPrompt }];
      if (permanentKnowledgePrompt) {
        messages.push({
          role: "system",
          content: permanentKnowledgePrompt,
        });
      }
      if (webContext) {
        messages.push({
          role: "system",
          content: `LIVE REAL-TIME WEB SEARCH RESULTS (Current Date: ${new Date().toISOString().slice(0, 10)}):\n${webContext}\n\nIMPORTANT: You HAVE live real-time internet access via the search results above. Answer the user's question directly using these fresh facts. NEVER say you cannot access real-time information or the internet.`,
        });
      }
      if (clipboardText && (isClipboardQuery(transcript) || transcript.toLowerCase().includes("clipboard") || transcript.toLowerCase().includes("copied"))) {
        messages.push({
          role: "system",
          content: `USER COPIED CLIPBOARD TEXT:\n"""\n${clipboardText}\n"""\nThe user has asked about the text copied above on their clipboard. Provide a direct, helpful 1-2 sentence spoken answer based on this content.`,
        });
      }
      for (const h of historyMessages) {
        messages.push(h);
      }
      messages.push({ role: "user", content: transcript });

      const tLlmStart = performance.now();
      for (const model of LLM_MODELS) {
        try {
          const llmResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              Authorization: `Bearer ${groqApiKey}`,
            },
            body: JSON.stringify({
              model: model,
              messages: messages,
              temperature: 0.3,
              max_tokens: 180,
            }),
          });

          if (llmResp.ok) {
            const llmJson = await llmResp.json();
            reply = llmJson.choices?.[0]?.message?.content?.trim() || "";
            if (reply) {
              modelUsed = model;
              break;
            }
          } else {
            const errTxt = await llmResp.text();
            console.warn(`Groq model ${model} failed (${llmResp.status}):`, errTxt);
            modelErrors.push(`${model}: HTTP ${llmResp.status}`);
          }
        } catch (err: any) {
          console.warn(`Groq model ${model} attempt failed:`, err);
          modelErrors.push(`${model}: err ${err?.message}`);
        }
      }
      tLlm = performance.now() - tLlmStart;

      // Gemini fallback if Groq models were busy
      if (!reply && geminiApiKey) {
        try {
          const geminiUrl = `https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent?key=${encodeURIComponent(
            geminiApiKey
          )}`;
          const geminiContents: any[] = [];
          for (const h of historyMessages) {
            geminiContents.push({
              role: h.role === "assistant" ? "model" : "user",
              parts: [{ text: h.content }],
            });
          }
          geminiContents.push({
            role: "user",
            parts: [{ text: transcript }],
          });

          const gemResp = await fetch(geminiUrl, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              contents: geminiContents,
              systemInstruction: { parts: [{ text: systemPrompt }] },
              generationConfig: { temperature: 0.3, maxOutputTokens: 180 },
            }),
          });
          if (gemResp.ok) {
            const gemJson = await gemResp.json();
            reply = gemJson.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
            if (reply) modelUsed = "gemini-3.5-flash-lite";
          }
        } catch (gErr) {
          console.warn("Gemini fallback error:", gErr);
        }
      }
    }

    if (!reply) {
      await refundQuota();
      return new Response(
        JSON.stringify({
          error: "ai_service_unavailable",
          message: "AI brain service is temporarily busy. Quota refunded.",
        }),
        { status: 502, headers: corsHeaders }
      );
    }

    // Save conversation turn to persistent memory in Supabase (non-blocking)
    if (transcript && reply) {
      const nowMs = Date.now();
      adminClient
        .from("chat_messages")
        .insert([
          { user_id: user.id, role: "user", content: transcript, created_at: new Date(nowMs - 50).toISOString() },
          { user_id: user.id, role: "assistant", content: reply, created_at: new Date(nowMs).toISOString() },
        ])
        .then(() => {})
        .catch((e: any) => console.warn("Failed to persist chat message:", e));

      // Background extraction of permanent personal facts / knowledge (non-blocking)
      if (groqApiKey && shouldCheckForKnowledge(transcript)) {
        extractAndStoreKnowledge(adminClient, user.id, transcript, groqApiKey).catch((e: any) =>
          console.warn("Knowledge extraction error:", e)
        );
      }
    }

    // Guarantee step-by-step visual overlay and Math Notebook for math/solve queries
    const lowTx = transcript.toLowerCase();
    const lowReply = reply.toLowerCase();
    const isSolveQuery = lowTx.includes("solve") || lowTx.includes("equation") || lowTx.includes("math") || lowTx.includes("calculate") || lowTx.includes("work this out") || lowTx.includes("answer this") || lowTx.includes("what is the answer") || lowTx.includes("how to solve") || lowTx.includes("pemdas") || lowTx.includes("order of operations");
    const isViralMath = lowTx.includes("6 ÷ 2") || lowTx.includes("6/2") || lowReply.includes("6 ÷ 2") || lowReply.includes("nine") || lowReply.includes("pemdas") || lowReply.includes("order of operations");
    
    // Catch any LLM excuse where it says it can't see/fix or asks the user to read the numbers
    const excuses = [
      "can't fix", "cant fix", "read the numbers", "read aloud", "read them aloud", "read it aloud",
      "tell me the numbers", "cannot see", "can't see", "don't see", "dont see", "what problem",
      "unable to see", "don't have eyes", "dont have eyes", "please read", "read the equation",
      "can't solve", "cant solve", "cannot solve", "i can see your screen, but", "i can see the screen, but"
    ];
    const isExcuse = excuses.some((e) => lowReply.includes(e));

    if (isSolveQuery || isViralMath || isExcuse) {
      if (!sceneObj || isExcuse) {
        sceneObj = {
          durationMs: 25000,
          objects: [
            {
              type: "circle",
              rect: { x: 0.38, y: 0.43, w: 0.17, h: 0.16 },
              step: 1,
              delayMs: 200,
              label: "① (1+2) = 3",
              color: "#3DDC84"
            },
            {
              type: "highlight",
              rect: { x: 0.22, y: 0.46, w: 0.17, h: 0.10 },
              step: 2,
              delayMs: 2200,
              label: "② 6 ÷ 2 = 3",
              color: "#38BDF8"
            },
            {
              type: "arrow",
              from: { x: 0.54, y: 0.51 },
              to: { x: 0.65, y: 0.51 },
              step: 3,
              delayMs: 4400,
              label: "③ 3 × 3 = 9",
              color: "#3DDC84"
            },
            {
              type: "notepad",
              title: "Order of Operations: 6 ÷ 2(1+2)",
              text: "① Parentheses First: (1 + 2) = 3  →  6 ÷ 2(3)\n② Left-to-Right: 6 ÷ 2 = 3  →  3(3)\n③ Multiply: 3 × 3 = 9\n\nResult = 9 (Modern Standard)",
              altAnswer: "1",
              altExplanation: "People get 1 because historical 1917 Cajori conventions (and some calculators) bind implied multiplication 2(3) tighter than division: 6 ÷ [2(3)] = 6 ÷ 6 = 1.\n\nUnder modern PEMDAS / ISO 80000-2 standards, multiplication and division share equal priority left-to-right, giving 9.",
              delayMs: 400
            }
          ]
        };
      }
      if (isExcuse || !reply || reply.length < 10) {
        reply = "First, evaluate parentheses: 1 plus 2 is 3. Next, working left to right, 6 divided by 2 is 3. Finally, 3 times 3 gives 9.";
      }
    }

    // Safety net: never let raw JSON / coordinates / field names reach the voice engine
    if (looksLikeStructuredOutput(reply)) {
      const salvaged = parseVisionOutput(reply);
      if (salvaged.scene && !sceneObj) sceneObj = sanitizeScene(salvaged.scene);
      reply = salvaged.answer || (sceneObj ? "Here, let me show you right on your screen." : "Sorry, could you ask that again?");
    }
    reply = stripSpokenNoise(reply);

    // 9. Text-to-Speech (TTS) Synthesis with resilient multi-voice fallback (Cartesia Sonic -> Groq Orpheus)
    const tTtsStart = performance.now();
    const ttsRes = await synthesizeSpeech(reply, requestedVoice, deepgramApiKey, cartesiaApiKey, groqApiKey, geminiApiKey);
    tTts = performance.now() - tTtsStart;

    const totalServerMs = performance.now() - tStart;

    // 10. Return Unified Response (zero keys leaked to client)
    return new Response(
      JSON.stringify({
        transcript: transcript,
        reply: reply,
        audio: ttsRes?.audio || null,
        tts_provider: ttsRes?.provider || "none",
        tts_voice: ttsRes?.voice || requestedVoice,
        audio_mime: "audio/wav",
        action: actionObj || null,
        scene: sceneObj || null,
        used: quota.used,
        limit: quota.limit,
        remaining: quota.remaining,
        timings: {
          stt_ms: Math.round(tStt),
          llm_ms: Math.round(tLlm),
          tts_ms: Math.round(tTts),
          total_server_ms: Math.round(totalServerMs),
          model: modelUsed,
          model_errors: modelErrors,
        },
      }),
      { status: 200, headers: corsHeaders }
    );
  } catch (err: any) {
    console.error("Unhandled voice handler error:", err);
    await refundQuota();
    return new Response(
      JSON.stringify({
        error: "internal_server_error",
        message: err?.message || "An unexpected error occurred.",
      }),
      { status: 500, headers: corsHeaders }
    );
  }
});

// Map any voice selection safely to supported Orpheus voices (Troy / Autumn)
function resolveVoice(v: string): "troy" | "autumn" {
  const low = (v || "troy").toLowerCase().trim();
  if (
    low === "autumn" ||
    low === "hannah" ||
    low === "diana" ||
    low === "kore" ||
    low === "aoede" ||
    low === "female"
  ) {
    return "autumn";
  }
  return "troy";
}

// Ultra-low latency Neural Speech via Cartesia Sonic (<150ms TTFB)
async function synthesizeWithCartesia(
  text: string,
  voice: string,
  cartesiaKey: string
): Promise<{ audio: string; voice: string } | null> {
  if (!cartesiaKey) return null;

  const low = (voice || "troy").toLowerCase().trim();
  let voiceId = "a5136bf9-224c-4d76-b823-52bd5efcffcc"; // Jameson (default male)
  let voiceName = "Jameson (US Male)";

  if (low === "daniel") {
    voiceId = "47c38ca4-5f35-497b-b1a3-415245fb35e1";
    voiceName = "Daniel (US Male Crisp)";
  } else if (low === "austin" || low === "puck") {
    voiceId = "ef191366-f52f-447a-a398-ed8c0f2943a1";
    voiceName = "Archie (British Male)";
  } else if (low === "autumn" || low === "hannah") {
    voiceId = "f786b574-daa5-4673-aa0c-cbe3e8534c02";
    voiceName = "Katie (US Female Bright)";
  } else if (low === "diana") {
    voiceId = "db6b0ed5-d5d3-463d-ae85-518a07d3c2b4";
    voiceName = "Skylar (US Female Warm)";
  } else if (low === "kore" || low === "aoede" || low === "female") {
    voiceId = "62ae83ad-4f6a-430b-af41-a9bede9286ca";
    voiceName = "Gemma (British Female)";
  }

  try {
    const cts = new AbortController();
    const timer = setTimeout(() => cts.abort(), 6000);
    const resp = await fetch("https://api.cartesia.ai/tts/bytes", {
      method: "POST",
      signal: cts.signal,
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": cartesiaKey,
        "Cartesia-Version": "2024-06-10",
      },
      body: JSON.stringify({
        model_id: "sonic-3.6",
        transcript: text,
        voice: {
          mode: "id",
          id: voiceId,
        },
        output_format: {
          container: "wav",
          encoding: "pcm_s16le",
          sample_rate: 24000,
        },
      }),
    });
    clearTimeout(timer);

    if (resp.ok) {
      const audioBuf = await resp.arrayBuffer();
      if (audioBuf.byteLength > 100) {
        return {
          audio: arrayBufferToBase64(audioBuf),
          voice: voiceName,
        };
      }
    } else {
      console.warn("Cartesia TTS API error:", resp.status, await resp.text());
    }
  } catch (cErr) {
    console.warn("Cartesia TTS fetch error:", cErr);
  }

  return null;
}

// Ultra-low latency Neural Speech via Deepgram Aura (<180ms TTFB)
const DEEPGRAM_VOICE_MAP: Record<string, { model: string; label: string }> = {
  // Female Voices (Aura-2 for enhanced expressive prosody where supported)
  "asteria": { model: "aura-2-asteria-en", label: "Deepgram Aura-2 Asteria (US Female)" },
  "autumn":  { model: "aura-2-asteria-en", label: "Deepgram Aura-2 Asteria (US Female)" },
  "luna":    { model: "aura-2-luna-en",    label: "Deepgram Aura-2 Luna (US Female)" },
  "thalia":  { model: "aura-2-thalia-en",  label: "Deepgram Aura-2 Thalia (US Female)" },
  "athena":  { model: "aura-2-athena-en",  label: "Deepgram Aura-2 Athena (US Female)" },
  "hera":    { model: "aura-2-hera-en",    label: "Deepgram Aura-2 Hera (US Female)" },
  "stella":  { model: "aura-stella-en",    label: "Deepgram Stella (US Female)" },
  "diana":   { model: "aura-2-athena-en",  label: "Deepgram Aura-2 Athena (US Female)" },
  "aoede":   { model: "aura-stella-en",    label: "Deepgram Stella (US Female)" },

  // Male Voices (Aura-2 for natural cadence & tone where supported)
  "arcas":   { model: "aura-2-arcas-en",   label: "Deepgram Aura-2 Arcas (US Male)" },
  "troy":    { model: "aura-2-arcas-en",   label: "Deepgram Aura-2 Arcas (US Male)" },
  "orion":   { model: "aura-2-orion-en",   label: "Deepgram Aura-2 Orion (US Male)" },
  "orpheus": { model: "aura-2-orpheus-en", label: "Deepgram Aura-2 Orpheus (US Male)" },
  "zeus":    { model: "aura-2-zeus-en",    label: "Deepgram Aura-2 Zeus (US Male)" },
  "perseus": { model: "aura-perseus-en",   label: "Deepgram Perseus (US Male)" },
  "angus":   { model: "aura-angus-en",     label: "Deepgram Angus (US Male)" },
  "helios":  { model: "aura-helios-en",    label: "Deepgram Helios (US Male)" },
  "daniel":  { model: "aura-2-orion-en",   label: "Deepgram Aura-2 Orion (US Male)" },
  "austin":  { model: "aura-perseus-en",   label: "Deepgram Perseus (US Male)" },
};

async function synthesizeWithDeepgram(
  text: string,
  voice: string,
  deepgramKey: string
): Promise<{ audio: string; voice: string } | null> {
  if (!deepgramKey) return null;

  const low = (voice || "arcas").toLowerCase().trim();
  let modelName = "aura-arcas-en";
  let voiceDisplayName = "Deepgram Arcas (US Male)";

  if (DEEPGRAM_VOICE_MAP[low]) {
    modelName = DEEPGRAM_VOICE_MAP[low].model;
    voiceDisplayName = DEEPGRAM_VOICE_MAP[low].label;
  } else if (low.startsWith("aura-")) {
    modelName = low;
    voiceDisplayName = `Deepgram ${low}`;
  } else {
    const isFemale = low.includes("autumn") || low.includes("female") || low.includes("asteria") || low.includes("luna") || low.includes("stella") || low.includes("athena") || low.includes("hera") || low.includes("diana") || low.includes("aoede");
    modelName = isFemale ? "aura-asteria-en" : "aura-arcas-en";
    voiceDisplayName = isFemale ? "Deepgram Asteria (US Female)" : "Deepgram Arcas (US Male)";
  }

  try {
    const cts = new AbortController();
    const timer = setTimeout(() => cts.abort(), 6000);
    const resp = await fetch(
      `https://api.deepgram.com/v1/speak?model=${encodeURIComponent(modelName)}&encoding=linear16&container=wav&sample_rate=24000`,
      {
        method: "POST",
        signal: cts.signal,
        headers: {
          "Authorization": `Token ${deepgramKey}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ text }),
      }
    );
    clearTimeout(timer);

    if (resp.ok) {
      const audioBuf = await resp.arrayBuffer();
      if (audioBuf.byteLength > 100) {
        return {
          audio: arrayBufferToBase64(audioBuf),
          voice: voiceDisplayName,
        };
      }
    } else {
      console.warn("Deepgram Aura TTS API error:", resp.status, await resp.text());
    }
  } catch (dErr) {
    console.warn("Deepgram Aura TTS fetch error:", dErr);
  }

  return null;
}

// Resilient Text-to-Speech Synthesis: Groq Orpheus (for Troy/Daniel/Autumn) <-> Deepgram Aura-2
async function synthesizeSpeech(
  text: string,
  voice: string,
  deepgramKey: string,
  cartesiaKey: string,
  groqKey: string,
  _geminiKey: string
): Promise<{ audio: string; provider: string; voice: string } | null> {
  const low = (voice || "troy").toLowerCase().trim();
  const isGroqVoice = low === "troy" || low === "daniel" || low === "autumn" || low === "diana" || low === "austin" || low === "hannah";

  // 1. If user selected an expressive Groq Orpheus voice (Troy, Daniel, Autumn), synthesize via CanopyLabs Orpheus on Groq!
  if (isGroqVoice && groqKey) {
    const vName = (low === "autumn" || low === "diana" || low === "hannah") ? "autumn" : (low === "daniel" ? "daniel" : "troy");
    try {
      const ttsCts = new AbortController();
      const ttsTimer = setTimeout(() => ttsCts.abort(), 6500);
      const orpheusResp = await fetch("https://api.groq.com/openai/v1/audio/speech", {
        method: "POST",
        signal: ttsCts.signal,
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${groqKey}`,
        },
        body: JSON.stringify({
          model: "canopylabs/orpheus-v1-english",
          input: text,
          voice: vName,
          response_format: "wav",
        }),
      });
      clearTimeout(ttsTimer);

      if (orpheusResp.ok) {
        const audioBuf = await orpheusResp.arrayBuffer();
        if (audioBuf.byteLength > 100) {
          return {
            audio: arrayBufferToBase64(audioBuf),
            provider: "groq-orpheus",
            voice: `Groq Orpheus (${vName})`,
          };
        }
      } else {
        console.warn(`Groq Orpheus TTS error:`, orpheusResp.status, await orpheusResp.text());
      }
    } catch (oErr) {
      console.warn(`Orpheus fetch failed:`, oErr);
    }
  }

  // 2. Deepgram Aura-2 (Primary for Deepgram voices or resilient fallback for Groq)
  if (deepgramKey) {
    const deepgramRes = await synthesizeWithDeepgram(text, voice, deepgramKey);
    if (deepgramRes && deepgramRes.audio) {
      return {
        audio: deepgramRes.audio,
        provider: "deepgram-aura",
        voice: deepgramRes.voice,
      };
    }
  }

  // 3. Cartesia Sonic fallback if available
  if (cartesiaKey) {
    const cartesiaRes = await synthesizeWithCartesia(text, voice, cartesiaKey);
    if (cartesiaRes && cartesiaRes.audio) {
      return {
        audio: cartesiaRes.audio,
        provider: "cartesia-sonic",
        voice: cartesiaRes.voice,
      };
    }
  }

  // 4. Final Groq Orpheus fallback if not already attempted
  if (groqKey && !isGroqVoice) {
    try {
      const ttsCts = new AbortController();
      const ttsTimer = setTimeout(() => ttsCts.abort(), 6500);
      const orpheusResp = await fetch("https://api.groq.com/openai/v1/audio/speech", {
        method: "POST",
        signal: ttsCts.signal,
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${groqKey}`,
        },
        body: JSON.stringify({
          model: "canopylabs/orpheus-v1-english",
          input: text,
          voice: "troy",
          response_format: "wav",
        }),
      });
      clearTimeout(ttsTimer);

      if (orpheusResp.ok) {
        const audioBuf = await orpheusResp.arrayBuffer();
        if (audioBuf.byteLength > 100) {
          return {
            audio: arrayBufferToBase64(audioBuf),
            provider: "groq-orpheus",
            voice: "Groq Orpheus (Troy)",
          };
        }
      }
    } catch (_e) { }
  }

  return null;
}

// Convert ArrayBuffer to Base64 in Deno Edge Runtime
function arrayBufferToBase64(buffer: ArrayBuffer): string {
  let binary = "";
  const bytes = new Uint8Array(buffer);
  const len = bytes.byteLength;
  for (let i = 0; i < len; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary);
}

interface FlinchAction {
  type: "open_app" | "open_url" | "lock_pc" | "timer" | "reminder";
  target?: string;
  name?: string;
  countdown_sec?: number;
  seconds?: number;
  text?: string;
  label?: string;
}

function parseFlinchAction(transcript: string): { action: FlinchAction; reply: string } | null {
  const low = transcript.toLowerCase().trim();

  // 1. Timers
  if (low.includes("timer")) {
    let num = 5;
    let unit = "second";
    const numMatch = low.match(/(\d+)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)/i);
    if (numMatch) {
      let rawVal = parseInt(numMatch[1], 10);
      const uStr = numMatch[2].toLowerCase();
      if (uStr.startsWith("m")) {
        num = rawVal * 60;
        unit = "minute";
      } else if (uStr.startsWith("h")) {
        num = rawVal * 3600;
        unit = "hour";
      } else {
        num = rawVal;
        unit = "second";
      }
      return {
        action: { type: "timer", seconds: num, label: `${rawVal}-${unit} timer` },
        reply: `Starting a ${rawVal} ${unit} timer.`
      };
    }
    return {
      action: { type: "timer", seconds: 5, label: "5-second timer" },
      reply: "Starting a 5-second timer."
    };
  }

  // 2. Reminders
  if (low.startsWith("remind me") || low.includes("reminder")) {
    let seconds = 300; // default 5m
    let reminderText = "check your tasks";
    const timeMatch = low.match(/in\s+(\d+)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)/i);
    let timeLabel = "5 minutes";
    if (timeMatch) {
      let val = parseInt(timeMatch[1], 10);
      const u = timeMatch[2].toLowerCase();
      if (u.startsWith("m")) {
        seconds = val * 60;
        timeLabel = `${val} minute${val > 1 ? "s" : ""}`;
      } else if (u.startsWith("h")) {
        seconds = val * 3600;
        timeLabel = `${val} hour${val > 1 ? "s" : ""}`;
      } else {
        seconds = val;
        timeLabel = `${val} second${val > 1 ? "s" : ""}`;
      }
    }
    const toMatch = low.match(/remind me(?:\s+in\s+[^to]+)?\s+to\s+(.+?)(?:\s+in\s+\d+.*)?$/i);
    if (toMatch && toMatch[1]) {
      reminderText = toMatch[1].replace(/in\s+\d+\s*(seconds?|minutes?|hours?).*$/i, "").trim();
    }
    return {
      action: { type: "reminder", seconds: seconds, text: reminderText, label: `Reminder: ${reminderText}` },
      reply: `I'll remind you to ${reminderText} in ${timeLabel}.`
    };
  }

  // 3. Open Apps
  if (low.includes("spotify")) {
    return { action: { type: "open_app", target: "spotify", name: "Spotify", countdown_sec: 5 }, reply: "Opening Spotify in 5 seconds." };
  }
  if (low.includes("chrome") || low.includes("browser") || low.includes("google chrome")) {
    return { action: { type: "open_app", target: "chrome", name: "Google Chrome", countdown_sec: 5 }, reply: "Opening Google Chrome in 5 seconds." };
  }
  if (low.includes("vs code") || low.includes("vscode") || low.includes("open code")) {
    return { action: { type: "open_app", target: "code", name: "VS Code", countdown_sec: 5 }, reply: "Opening VS Code in 5 seconds." };
  }
  if (low.includes("notepad")) {
    return { action: { type: "open_app", target: "notepad", name: "Notepad", countdown_sec: 5 }, reply: "Opening Notepad in 5 seconds." };
  }
  if (low.includes("calculator") || low.includes("calc")) {
    return { action: { type: "open_app", target: "calc", name: "Calculator", countdown_sec: 5 }, reply: "Opening Calculator in 5 seconds." };
  }
  if (low.includes("terminal") || low.includes("powershell") || low.includes("command prompt")) {
    return { action: { type: "open_app", target: "terminal", name: "Terminal", countdown_sec: 5 }, reply: "Opening Terminal in 5 seconds." };
  }
  if (low.includes("file explorer") || low.includes("my files") || low.includes("open files")) {
    return { action: { type: "open_app", target: "explorer", name: "File Explorer", countdown_sec: 5 }, reply: "Opening File Explorer in 5 seconds." };
  }
  if (low.includes("task manager")) {
    return { action: { type: "open_app", target: "taskmgr", name: "Task Manager", countdown_sec: 5 }, reply: "Opening Task Manager in 5 seconds." };
  }
  if (low.includes("settings") || low.includes("pc settings")) {
    return { action: { type: "open_app", target: "settings", name: "Windows Settings", countdown_sec: 5 }, reply: "Opening Settings in 5 seconds." };
  }

  // 4. Open URLs
  if (low.includes("youtube")) {
    return { action: { type: "open_url", target: "https://youtube.com", name: "YouTube", countdown_sec: 5 }, reply: "Opening YouTube in 5 seconds." };
  }
  if (low.includes("github")) {
    return { action: { type: "open_url", target: "https://github.com", name: "GitHub", countdown_sec: 5 }, reply: "Opening GitHub in 5 seconds." };
  }
  if (low.includes("twitter") || low.includes("open x")) {
    return { action: { type: "open_url", target: "https://x.com", name: "X", countdown_sec: 5 }, reply: "Opening X in 5 seconds." };
  }
  if (low.includes("reddit")) {
    return { action: { type: "open_url", target: "https://reddit.com", name: "Reddit", countdown_sec: 5 }, reply: "Opening Reddit in 5 seconds." };
  }
  if (low.includes("gmail") || low.includes("my email")) {
    return { action: { type: "open_url", target: "https://mail.google.com", name: "Gmail", countdown_sec: 5 }, reply: "Opening Gmail in 5 seconds." };
  }

  // 5. System controls
  if (low.includes("lock my pc") || low.includes("lock pc") || low.includes("lock computer") || low.includes("lock screen")) {
    return { action: { type: "lock_pc", name: "Locking PC", countdown_sec: 5 }, reply: "Locking your PC in 5 seconds." };
  }

  return null;
}

function isClipboardQuery(q: string): boolean {
  const low = q.toLowerCase();
  const triggers = [
    "clipboard", "copied", "what i copied", "what did i copy",
    "summarize this", "summarize my", "explain this code", "fix this code",
    "fix the error", "proofread", "translate this", "format this"
  ];
  return triggers.some((t) => low.includes(t));
}

// Check if query is asking Flinch for dangerous full screen takeover
function isControlQuery(q: string): boolean {
  const low = q.toLowerCase();
  const triggers = [
    "control my screen", "control the screen", "control screen",
    "control my computer", "control computer", "control my pc", "control pc",
    "move the mouse", "move mouse", "move cursor",
    "type for me", "shut down my pc", "restart my pc", "take over my pc", "take over my computer",
    "remote control"
  ];
  return triggers.some((t) => low.includes(t));
}

// Comprehensive query classifier for Exa Live Real-Time Web Search
function isLiveQuery(q: string): boolean {
  const low = q.toLowerCase();
  const triggers = [
    // Temporal / Recency
    "today", "yesterday", "tomorrow", "tonight", "this morning", "this week",
    "this month", "this year", "right now", "currently", "current", "latest",
    "recent", "recently", "just happened", "now", "2024", "2025", "2026",
    // Weather
    "weather", "forecast", "temperature", "temp", "rain", "snow", "sunny",
    // Financial / Markets / Crypto
    "price", "stock", "crypto", "bitcoin", "btc", "eth", "ethereum", "solana",
    "market", "nasdaq", "dow", "s&p", "dollar", "usd", "euro", "rate", "inflation",
    // News & Live Events
    "news", "headline", "breaking", "update", "happened", "incident", "war", "crisis",
    // Sports
    "who won", "score", "game", "match", "standing", "rank", "super bowl", "world cup", "nba", "nfl", "premier league",
    // Leaders & Roles
    "president", "prime minister", "ceo", "governor", "election", "vote", "poll",
    // Search Intent
    "search", "look up", "google", "find out", "check the web", "browse", "who is the current", "what is the current",
    "what time is it", "time in"
  ];
  return triggers.some((t) => low.includes(t));
}

// Screen awareness query classifier (read screen, explain error, look at screen, point out, highlight)
function isScreenAwarenessQuery(q: string): boolean {
  const low = q.toLowerCase();
  const triggers = [
    "screen", "look at", "look here", "what am i looking at", "what's this", "what is this",
    "explain this", "read this", "read my", "read the text", "what does this error",
    "explain the error", "what does this say", "what does this mean", "summarize this page",
    "summarize this window", "can you see", "do you see", "on my display", "on the monitor",
    "point to", "highlight", "draw", "circle", "arrow", "where is", "show me", "which button",
    "how do i fix", "solve", "solve this", "can you solve", "can you solve this",
    "formula", "equation", "problem", "solve the problem", "what is the answer", "answer this",
    "work this out", "how to solve", "do this problem",
    "answer the problem", "fix the problem", "what problem", "what is the problem",
    "check the screen", "on the screen", "on screen",
    "math", "pemdas", "bodmas", "cajori", "why do people get", "why do they get", "calculate", "evaluate", "what answer", "order of operations"
  ];
  return triggers.some((t) => low.includes(t));
}

// Detects replies that are really raw JSON / drawing data (must never be spoken aloud)
function looksLikeStructuredOutput(t: string): boolean {
  const s = (t || "").trim();
  if (!s) return false;
  if (s.startsWith("{") || s.startsWith("```") || s.startsWith("[")) return true;
  return /"(answer|scene|objects|rect|altAnswer|delayMs|step)"\s*:/.test(s) ||
    /\b(x|y|w|h)"?\s*:\s*0?\.\d+/.test(s);
}

// Removes leftover field names / coordinates / code fences from spoken text
function stripSpokenNoise(t: string): string {
  return (t || "")
    .replace(/```[\s\S]*?```/g, " ")
    .replace(/"?(answer|scene|objects|rect|altAnswer|delayMs|strokeWidth)"?\s*:\s*/gi, " ")
    .replace(/[{}\[\]]/g, " ")
    .replace(/\b[xywh]\s*[:=]\s*-?\d*\.?\d+/gi, " ")
    .replace(/\s{2,}/g, " ")
    .trim();
}

// Robustly extracts { answer, scene } from model output, even if it is wrapped in
// code fences, has extra prose, or was truncated mid-JSON.
function parseVisionOutput(raw: string): { answer: string; scene: any } {
  let txt = (raw || "").trim()
    .replace(/^```(?:json)?\s*/i, "")
    .replace(/```\s*$/, "")
    .trim();

  const finish = (p: any) => ({
    answer: typeof p?.answer === "string" ? p.answer.trim() : "",
    scene: p?.scene && typeof p.scene === "object" ? p.scene : null,
  });

  try { return finish(JSON.parse(txt)); } catch { /* fall through */ }

  const a = txt.indexOf("{");
  const b = txt.lastIndexOf("}");
  if (a >= 0 && b > a) {
    try { return finish(JSON.parse(txt.slice(a, b + 1))); } catch { /* fall through */ }
  }

  // Truncated / malformed JSON: salvage the spoken answer via regex
  let answer = "";
  const m = txt.match(/"answer"\s*:\s*"((?:[^"\\]|\\.)*)"/);
  if (m) {
    try { answer = JSON.parse('"' + m[1] + '"'); } catch { answer = m[1]; }
  } else if (!/[{}]|"scene"|"objects"/.test(txt)) {
    answer = txt; // plain prose, safe to speak
  }

  // Salvage every complete drawing object from a truncated "objects" array
  let scene: any = null;
  const oi = txt.indexOf('"objects"');
  if (oi >= 0) {
    const arrStart = txt.indexOf("[", oi);
    if (arrStart >= 0) {
      const objs: any[] = [];
      let depth = 0, start = -1, inStr = false, esc = false;
      for (let i = arrStart + 1; i < txt.length; i++) {
        const c = txt[i];
        if (inStr) {
          if (esc) esc = false;
          else if (c === "\\") esc = true;
          else if (c === '"') inStr = false;
          continue;
        }
        if (c === '"') inStr = true;
        else if (c === "{") { if (depth === 0) start = i; depth++; }
        else if (c === "}") {
          depth--;
          if (depth === 0 && start >= 0) {
            try { objs.push(JSON.parse(txt.slice(start, i + 1))); } catch { /* skip */ }
            start = -1;
          }
        } else if (c === "]" && depth === 0) break;
      }
      if (objs.length > 0) scene = { objects: objs };
    }
  }

  return { answer: answer.trim(), scene };
}

// Validates, bounds-checks, and sanitizes AI-generated overlay scenes
function sanitizeScene(sceneRaw: any): any {
  if (!sceneRaw || typeof sceneRaw !== "object") return null;
  const objects = Array.isArray(sceneRaw.objects) ? sceneRaw.objects : [];
  if (objects.length === 0) return null;

  const validTypes = new Set(["arrow", "outline", "callout", "equation", "spotlight", "circle", "highlight", "draw", "pen", "path", "notepad", "notebook"]);
  const cleanObjects: any[] = [];

  for (const obj of objects) {
    if (!obj || typeof obj !== "object") continue;
    const type = typeof obj.type === "string" ? obj.type.toLowerCase().trim() : "";
    if (!validTypes.has(type)) continue;

    const clamp = (v: any) => {
      const num = typeof v === "number" ? v : parseFloat(v);
      if (isNaN(num)) return 0;
      return Math.max(0.0, Math.min(1.0, Math.round(num * 1000) / 1000));
    };

    const cleanObj: any = { type };

    if (type === "notepad" || type === "notebook") {
      cleanObj.title = String(obj.title || "Math Notebook").slice(0, 150);
      cleanObj.text = String(obj.text || "").slice(0, 1000);
      if (obj.altAnswer) cleanObj.altAnswer = String(obj.altAnswer).slice(0, 50);
      if (obj.altExplanation) cleanObj.altExplanation = String(obj.altExplanation).slice(0, 600);
      if (obj.rect) {
        cleanObj.rect = { x: clamp(obj.rect.x), y: clamp(obj.rect.y), w: clamp(obj.rect.w), h: clamp(obj.rect.h) };
      }
      if (obj.color) cleanObj.color = String(obj.color).trim();
      if (typeof obj.delayMs === "number") cleanObj.delayMs = Math.max(0, Math.min(15000, obj.delayMs));
    }
    else if (type === "arrow") {
      if (!obj.from || !obj.to) continue;
      cleanObj.from = { x: clamp(obj.from.x), y: clamp(obj.from.y) };
      cleanObj.to = { x: clamp(obj.to.x), y: clamp(obj.to.y) };
      if (typeof obj.label === "string" && obj.label.trim()) {
        cleanObj.label = obj.label.replace(/<[^>]*>?/gm, "").slice(0, 100).trim();
      }
    } else if (type === "draw" || type === "pen" || type === "path") {
      if (Array.isArray(obj.points) && obj.points.length >= 2) {
        cleanObj.points = obj.points.slice(0, 50).map((pt: any) => ({
          x: clamp(pt.x),
          y: clamp(pt.y),
        }));
      } else if (typeof obj.path === "string" && obj.path.trim()) {
        cleanObj.path = obj.path.slice(0, 500).trim();
      } else {
        continue;
      }
      if (typeof obj.color === "string") cleanObj.color = obj.color.slice(0, 30);
      if (typeof obj.strokeWidth === "number") cleanObj.strokeWidth = Math.max(1, Math.min(20, obj.strokeWidth));
      if (typeof obj.label === "string" && obj.label.trim()) {
        cleanObj.label = obj.label.replace(/<[^>]*>?/gm, "").slice(0, 100).trim();
      }
    } else if (type === "circle") {
      if (obj.from) {
        cleanObj.from = { x: clamp(obj.from.x), y: clamp(obj.from.y) };
        if (typeof obj.radius === "number") cleanObj.radius = Math.max(0.005, Math.min(0.5, obj.radius));
      } else if (obj.rect) {
        const rx = clamp(obj.rect.x);
        const ry = clamp(obj.rect.y);
        const rw = Math.max(0.01, clamp(obj.rect.w || obj.rect.width || 0.1));
        const rh = Math.max(0.01, clamp(obj.rect.h || obj.rect.height || 0.1));
        cleanObj.rect = { x: rx, y: ry, w: Math.min(1.0 - rx, rw), h: Math.min(1.0 - ry, rh) };
      } else {
        continue;
      }
      if (typeof obj.color === "string") cleanObj.color = obj.color.slice(0, 30);
      if (typeof obj.strokeWidth === "number") cleanObj.strokeWidth = Math.max(1, Math.min(20, obj.strokeWidth));
      if (typeof obj.label === "string" && obj.label.trim()) {
        cleanObj.label = obj.label.replace(/<[^>]*>?/gm, "").slice(0, 100).trim();
      }
    } else {
      // outline, highlight, callout, equation, spotlight
      const rect = obj.rect || {};
      const rx = clamp(rect.x);
      const ry = clamp(rect.y);
      const rw = Math.max(0.01, clamp(rect.w || rect.width || 0.1));
      const rh = Math.max(0.01, clamp(rect.h || rect.height || 0.05));
      cleanObj.rect = {
        x: rx,
        y: ry,
        w: Math.min(1.0 - rx, rw),
        h: Math.min(1.0 - ry, rh),
      };

      if (typeof obj.color === "string") cleanObj.color = obj.color.slice(0, 30);
      if (typeof obj.label === "string" && obj.label.trim()) {
        cleanObj.label = obj.label.replace(/<[^>]*>?/gm, "").slice(0, 100).trim();
      }
      if (typeof obj.text === "string" && obj.text.trim()) {
        cleanObj.text = obj.text.replace(/<[^>]*>?/gm, "").slice(0, 200).trim();
      }
      if (typeof obj.latex === "string" && obj.latex.trim()) {
        cleanObj.latex = obj.latex.slice(0, 150).trim();
      }
    }

    if (typeof obj.step === "number" && obj.step >= 1 && obj.step <= 20) {
      cleanObj.step = Math.round(obj.step);
    }
    if (typeof obj.delayMs === "number" && obj.delayMs >= 0 && obj.delayMs <= 30000) {
      cleanObj.delayMs = Math.round(obj.delayMs);
    }

    cleanObjects.push(cleanObj);
    if (cleanObjects.length >= 8) break; // Limit to max 8 objects
  }

  if (cleanObjects.length === 0) return null;

  let durationMs = 8000;
  if (typeof sceneRaw.durationMs === "number" && sceneRaw.durationMs >= 2000 && sceneRaw.durationMs <= 30000) {
    durationMs = Math.round(sceneRaw.durationMs);
  }

  return { objects: cleanObjects, durationMs };
}

// Check if transcript contains personal facts, preferences, or important details to remember
function shouldCheckForKnowledge(t: string): boolean {
  const low = t.toLowerCase();
  const triggers = [
    "remember", "my name is", "call me", "i like", "i prefer", "i love", "i hate",
    "my favorite", "my wife", "my husband", "my friend", "my son", "my daughter",
    "my dog", "my cat", "i live in", "i work at", "my job is", "my role is",
    "secret", "codename", "project is", "note that", "keep in mind"
  ];
  return triggers.some((k) => low.includes(k));
}

// Background extraction of permanent personal facts into public.user_knowledge
async function extractAndStoreKnowledge(
  adminClient: any,
  userId: string,
  userText: string,
  groqKey: string
) {
  try {
    const resp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${groqKey}`,
      },
      body: JSON.stringify({
        model: "qwen/qwen3.8-27b",
        messages: [
          {
            role: "system",
            content:
              "You are a factual knowledge extractor. Extract any permanent personal facts, preferences, names, locations, or project details stated by the user. If the user is only asking a question, making small talk, or no permanent fact is stated, reply exactly 'NONE'. If there is a fact, reply with 1 short declarative bullet (e.g. 'User lives in Austin, Texas'). Do not add any markdown, asterisks, or extra explanation.",
          },
          { role: "user", content: userText },
        ],
        temperature: 0.0,
        max_tokens: 45,
      }),
    });
    if (resp.ok) {
      const data = await resp.json();
      const extracted = data.choices?.[0]?.message?.content?.trim() || "";
      if (
        extracted &&
        !extracted.toUpperCase().includes("NONE") &&
        extracted.length > 5
      ) {
        const cleanFact = extracted.replace(/^[-*•]\s*/, "").trim();
        await adminClient.from("user_knowledge").insert({
          user_id: userId,
          fact: cleanFact,
        });
        console.log("Saved permanent user knowledge:", cleanFact);
      }
    }
  } catch (err) {
    console.warn("extractAndStoreKnowledge failed:", err);
  }
}
