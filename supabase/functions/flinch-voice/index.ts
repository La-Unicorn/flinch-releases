// ============================================================================
// FLINCH AI: SUPABASE EDGE FUNCTION (flinch-voice)
// Single-Hop Speech-to-Text (Groq Whisper) + LLM Response + Orpheus TTS
// Beta Free Tier Quota Enforced: 15 Agent Tasks Per Month Per Account
// Zero API Keys Exposed To Client Desktop Application
// ============================================================================

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "npm:@supabase/supabase-js@2.39.0";

const MONTHLY_BETA_TASK_LIMIT = 15;
const EXEMPT_EMAIL_SHA256 = "ae862eb9f44cf720332541650c13e5b107ce803e9cd5f07ac7462c5ecc4c6eff";

async function sha256Hex(input: string): Promise<string> {
  const bytes = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

let _dbHardened = false;
async function ensureDbRpcHardened() {
  if (_dbHardened) return;
  _dbHardened = true;
  const dbUrl = Deno.env.get("SUPABASE_DB_URL");
  if (!dbUrl) return;
  try {
    const { Client } = await import("https://deno.land/x/postgres@v0.17.0/mod.ts");
    const client = new Client(dbUrl);
    await client.connect();
    await client.queryArray(`
      alter table public.profiles alter column voice_message_limit set default 15;
      update public.profiles set voice_message_limit = 15 where voice_message_limit < 15;
      create or replace function public.get_flinch_runtime_config()
      returns jsonb language plpgsql security definer set search_path = public as $$
      declare
        uid uuid := auth.uid();
        prof public.profiles%rowtype;
      begin
        if uid is null then raise exception 'not_authenticated'; end if;
        insert into public.profiles (id, email, voice_messages_used, voice_message_limit)
        select uid, u.email, 0, 15 from auth.users u where u.id = uid on conflict (id) do nothing;
        update public.profiles
        set voice_messages_used = case when date_trunc('month', updated_at) < date_trunc('month', now()) then 0 else voice_messages_used end,
            voice_message_limit = greatest(voice_message_limit, 15)
        where id = uid;
        select * into prof from public.profiles where id = uid;
        return jsonb_build_object(
          'preferred_voice', coalesce(prof.preferred_voice, 'troy'),
          'voice_used',      coalesce(prof.voice_messages_used, 0),
          'voice_limit',     coalesce(prof.voice_message_limit, 15),
          'voice_remaining', greatest(0, coalesce(prof.voice_message_limit, 15) - coalesce(prof.voice_messages_used, 0)),
          'tier',            coalesce(prof.tier, 'free')
        );
      end;
      $$;
    `);
    await client.end();
  } catch (e) {
    console.warn("DB RPC hardening skipped:", e);
  }
}

async function ensureMonthlyProfileState(adminClient: any, userId: string, userEmail: string) {
  await ensureDbRpcHardened();
  const now = new Date();
  let preferredVoice = "troy";
  let tier = "free";
  let used = 0;
  let limit = MONTHLY_BETA_TASK_LIMIT;

  try {
    const { data: prof } = await adminClient
      .from("profiles")
      .select("id, preferred_voice, tier, voice_messages_used, voice_message_limit, updated_at")
      .eq("id", userId)
      .maybeSingle();

    if (!prof) {
      await adminClient.from("profiles").insert({
        id: userId,
        email: userEmail || "",
        preferred_voice: "troy",
        tier: "free",
        voice_messages_used: 0,
        voice_message_limit: MONTHLY_BETA_TASK_LIMIT,
        updated_at: now.toISOString(),
      });
    } else {
      preferredVoice = prof.preferred_voice || "troy";
      tier = prof.tier || "free";
      used = Number(prof.voice_messages_used) || 0;
      limit = Math.max(MONTHLY_BETA_TASK_LIMIT, Number(prof.voice_message_limit) || MONTHLY_BETA_TASK_LIMIT);

      const prevDate = prof.updated_at ? new Date(prof.updated_at) : now;
      const isNewMonth =
        prevDate.getUTCFullYear() !== now.getUTCFullYear() ||
        prevDate.getUTCMonth() !== now.getUTCMonth();

      if (isNewMonth) {
        used = 0;
      }

      if (isNewMonth || (Number(prof.voice_message_limit) || 0) < MONTHLY_BETA_TASK_LIMIT) {
        await adminClient
          .from("profiles")
          .update({
            voice_messages_used: used,
            voice_message_limit: limit,
            updated_at: now.toISOString(),
          })
          .eq("id", userId);
      }
    }
  } catch (err) {
    console.warn("ensureMonthlyProfileState warning:", err);
  }

  return {
    preferred_voice: preferredVoice,
    tier,
    voice_used: used,
    voice_limit: limit,
    voice_remaining: Math.max(0, limit - used),
  };
}

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
  "llama-3.3-70b-versatile",
  "openai/gpt-oss-120b",
  "qwen/qwen3.8-27b",
  "openai/gpt-oss-20b"
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

  const emailHash = await sha256Hex((user.email || "").toLowerCase().trim());
  const profState = await ensureMonthlyProfileState(adminClient, user.id, user.email || "");
  const isExempt =
    emailHash === EXEMPT_EMAIL_SHA256 ||
    profState.tier === "pro" ||
    profState.tier === "admin" ||
    profState.tier === "unlimited";

  const contentType = req.headers.get("content-type") || "";

  // 2. JSON action handlers (get_config, update_voice, write_document, transform_text, translate_screen, tts)
  if (contentType.includes("application/json")) {
    try {
      const body = await req.json();

      if (body.action === "get_config") {
        return new Response(
          JSON.stringify({
            preferred_voice: profState.preferred_voice,
            voice_used: profState.voice_used,
            voice_limit: profState.voice_limit,
            voice_remaining: isExempt ? 999999 : profState.voice_remaining,
            tier: profState.tier,
          }),
          { status: 200, headers: corsHeaders }
        );
      }

      if (body.action === "update_voice") {
        const newVoice = String(body.voice || body.new_voice || "troy").trim() || "troy";
        await adminClient
          .from("profiles")
          .update({ preferred_voice: newVoice, updated_at: new Date().toISOString() })
          .eq("id", user.id);
        return new Response(JSON.stringify({ ok: true, preferred_voice: newVoice }), {
          status: 200,
          headers: corsHeaders,
        });
      }

      if (
        (body.action === "write_document" ||
          body.action === "generate_content" ||
          body.action === "transform_text" ||
          body.action === "translate_screen") &&
        !isExempt &&
        profState.voice_remaining <= 0 &&
        profState.voice_used > profState.voice_limit
      ) {
        return new Response(
          JSON.stringify({
            error: "limit_reached",
            used: profState.voice_used,
            limit: profState.voice_limit,
            remaining: 0,
            message: `Monthly beta limit reached: ${profState.voice_limit} of ${profState.voice_limit} agent tasks used this month.`,
          }),
          { status: 429, headers: corsHeaders }
        );
      }

      if (body.action === "write_document" || body.action === "generate_content") {
        const topic = String(body.topic || body.title || "").trim();
        const docType = String(body.type || "essay").trim();
        const isPresentation = docType === "presentation" || docType === "slides";

        let topicImageUrl = "";
        try {
          const wResp = await fetch(`https://en.wikipedia.org/api/rest_v1/page/summary/${encodeURIComponent(topic.replace(/\s+/g, "_"))}`, {
            headers: { "User-Agent": "Mozilla/5.0 (compatible; FlinchDesktop/1.0)" }
          });
          if (wResp.ok) {
            const wj = await wResp.json();
            topicImageUrl = wj.thumbnail?.source || wj.originalimage?.source || "";
          }
        } catch (_w) {}

        const systemAuthorRule = isPresentation
          ? `You are an expert keynote creator. Create a clean, elegant 6-slide presentation with spoken voiceovers about: ${topic}.
Do not include category badges, label tags, or AI slop.
Return strictly a valid JSON object without markdown fences:
{
  "title": "Presentation Title",
  "slides": [
    {
      "heading": "Slide Heading",
      "bullets": ["High-impact factual point 1", "Point 2", "Point 3"],
      "voiceover": "Natural, articulate spoken voiceover script that explains this slide aloud to the audience."
    }
  ]
}`
          : `You are an expert author writing a comprehensive ${docType}.
CRITICAL INSTRUCTIONS:
1. Output ONLY the document title, section headers, and body paragraphs of the ${docType}.
2. NEVER include conversational chatter, greetings, user guidance, instructions on how to copy/paste text, or AI disclaimers (e.g. NEVER write "Since I am an AI...", "Here is the essay...", or mention Discord, WhatsApp, or saving files).
3. Start directly with the title and content.`;

        const userPrompt = isPresentation
          ? `Create a 6-slide presentation with voiceover narration about: ${topic}. Include introduction, background, major achievements, current work, inspiring quotes, and future impact.`
          : `Write a comprehensive, engaging, well-structured ${docType} about: ${topic}.`;

        let generatedContent = "";

        // 1. Primary: Gemini 3.5 Flash (Fast, reliable, high quality)
        if (geminiApiKey) {
          const geminiModels = ["gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash"];
          for (const gm of geminiModels) {
            try {
              const gResp = await fetch(
                `https://generativelanguage.googleapis.com/v1beta/models/${gm}:generateContent?key=${encodeURIComponent(geminiApiKey)}`,
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
                        parts: [{ text: userPrompt }]
                      }
                    ],
                    generationConfig: {
                      temperature: 0.6,
                      maxOutputTokens: 4096,
                      ...(isPresentation ? { responseMimeType: "application/json" } : {})
                    }
                  })
                }
              );
              if (gResp.ok) {
                const gData = await gResp.json();
                generatedContent = gData.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
                if (generatedContent) break;
              }
            } catch (_e) {}
          }
        }

        // 2. Fallback: Groq LPU (Qwen / Llama)
        if (!generatedContent && groqApiKey) {
          for (const model of LLM_MODELS) {
            try {
              const grResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
                method: "POST",
                headers: {
                  "Authorization": `Bearer ${groqApiKey}`,
                  "Content-Type": "application/json"
                },
                body: JSON.stringify({
                  model: model,
                  messages: [
                    { role: "system", content: systemAuthorRule },
                    { role: "user", content: userPrompt }
                  ],
                  temperature: 0.6,
                  max_tokens: 3500
                })
              });
              if (grResp.ok) {
                const grData = await grResp.json();
                generatedContent = grData.choices?.[0]?.message?.content?.trim() || "";
                if (generatedContent) break;
              }
            } catch (_e) {}
          }
        }

        if (generatedContent) {
          if (isPresentation) {
            let deck: any = null;
            try {
              const cleanJson = generatedContent.replace(/^```(?:json)?\s*/i, "").replace(/```\s*$/, "").trim();
              deck = JSON.parse(cleanJson);
            } catch {
              deck = {
                title: topic,
                slides: [
                  { heading: topic, bullets: ["Key milestones and background", "Core achievements", "Impact and legacy"], voiceover: `Welcome to this presentation on ${topic}. Let us explore the key background, achievements, and future impact.` }
                ]
              };
            }
            generatedContent = buildHtmlPresentation(topic, topicImageUrl, deck);
          } else {
            generatedContent = generatedContent.replace(/^(Since I am an AI|As an AI|I cannot directly access|Here is the).*?\n+/gis, "").trim();
          }

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

      if (body.action === "transform_text") {
        const srcText = String(body.text_input || "").slice(0, 8000).trim();
        const instruction = String(body.instruction || "Summarize this text.").slice(0, 400).trim();
        if (!srcText) {
          return new Response(JSON.stringify({ error: "empty_text" }), { status: 400, headers: corsHeaders });
        }
        const sysRule = `You transform text for the user exactly as instructed.
RULES: Output ONLY the transformed text. No preamble, no quotes around it, no markdown, no explanations, no "Here is". Keep the original language unless asked to translate.`;
        let out = "";
        if (groqApiKey) {
          try {
            const gr = await fetch("https://api.groq.com/openai/v1/chat/completions", {
              method: "POST",
              headers: { "Authorization": `Bearer ${groqApiKey}`, "Content-Type": "application/json" },
              body: JSON.stringify({
                model: "llama-3.3-70b-versatile",
                messages: [
                  { role: "system", content: sysRule },
                  { role: "user", content: `INSTRUCTION: ${instruction}\n\nTEXT:\n"""\n${srcText}\n"""` },
                ],
                temperature: 0.3,
                max_tokens: 1800,
              }),
            });
            if (gr.ok) {
              const gj = await gr.json();
              out = (gj.choices?.[0]?.message?.content || "").trim();
            }
          } catch (_e) { /* fall through to Gemini */ }
        }
        if (!out && geminiApiKey) {
          try {
            const gm = await fetch(
              `https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key=${encodeURIComponent(geminiApiKey)}`,
              {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  systemInstruction: { parts: [{ text: sysRule }] },
                  contents: [{ role: "user", parts: [{ text: `INSTRUCTION: ${instruction}\n\nTEXT:\n"""\n${srcText}\n"""` }] }],
                  generationConfig: { temperature: 0.3, maxOutputTokens: 1800 },
                }),
              }
            );
            if (gm.ok) {
              const gj = await gm.json();
              out = (gj.candidates?.[0]?.content?.parts?.[0]?.text || "").trim();
            }
          } catch (_e) { /* ignore */ }
        }
        out = out.replace(/^```[a-z]*\s*/i, "").replace(/```\s*$/, "").trim();
        if (out) {
          return new Response(JSON.stringify({ result: out }), { status: 200, headers: corsHeaders });
        }
        return new Response(
          JSON.stringify({ error: "transform_failed", message: "Could not transform the text." }),
          { status: 502, headers: corsHeaders }
        );
      }

      if (body.action === "translate_screen") {
        const base64Image = String(body.screen || "").trim();
        const targetLanguage = String(body.target_language || "English").trim();
        if (!base64Image) {
          return new Response(JSON.stringify({ error: "missing_screen" }), { status: 400, headers: corsHeaders });
        }
        let translatedText = "";
        const promptText = `Read the prominent text or article visible on this screen/page and translate it accurately and naturally into ${targetLanguage}. Keep it conversational and suitable for text-to-speech. Provide 1 to 3 concise, clear sentences capturing the translation. Output ONLY the translation. No preamble, no disclaimers.`;

        // 1. Gemini Flash Vision
        if (geminiApiKey) {
          try {
            const gResp = await fetch(
              `https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=${encodeURIComponent(geminiApiKey)}`,
              {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  contents: [
                    {
                      parts: [
                        { text: promptText },
                        { inline_data: { mime_type: "image/jpeg", data: base64Image } }
                      ]
                    }
                  ],
                  generationConfig: { temperature: 0.2, maxOutputTokens: 600 }
                })
              }
            );
            if (gResp.ok) {
              const gData = await gResp.json();
              translatedText = gData.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
            }
          } catch (_e) {}
        }

        // 2. Fallback Groq Llama 3.2 Vision
        if (!translatedText && groqApiKey) {
          try {
            const grResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
              method: "POST",
              headers: { "Authorization": `Bearer ${groqApiKey}`, "Content-Type": "application/json" },
              body: JSON.stringify({
                model: "llama-3.2-11b-vision-preview",
                messages: [
                  {
                    role: "user",
                    content: [
                      { type: "text", text: promptText },
                      { type: "image_url", image_url: { url: `data:image/jpeg;base64,${base64Image}` } }
                    ]
                  }
                ],
                temperature: 0.2,
                max_tokens: 600
              })
            });
            if (grResp.ok) {
              const grData = await grResp.json();
              translatedText = grData.choices?.[0]?.message?.content?.trim() || "";
            }
          } catch (_e) {}
        }

        if (translatedText) {
          return new Response(JSON.stringify({ result: translatedText, language: targetLanguage }), {
            status: 200,
            headers: corsHeaders
          });
        }
        return new Response(JSON.stringify({ error: "translation_failed" }), { status: 502, headers: corsHeaders });
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

  // 3. Quota check: 15 agent tasks per month for free tier beta demo accounts
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
          message: "Database error verifying monthly task quota.",
        }),
        { status: 500, headers: corsHeaders }
      );
    }

    if (!qData || !qData.allowed) {
      const used = qData ? qData.used : MONTHLY_BETA_TASK_LIMIT;
      const limit = qData ? qData.limit : MONTHLY_BETA_TASK_LIMIT;
      return new Response(
        JSON.stringify({
          error: "limit_reached",
          used: used,
          limit: limit,
          remaining: 0,
          message: `Monthly beta limit reached: ${used} of ${limit} agent tasks used this month.`,
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
    const directText = String(formData.get("text") || formData.get("transcript") || "").trim();
    let screenB64 = "";
    if (screenFile && screenFile instanceof Blob && screenFile.size > 500) {
      try {
        const sBytes = await screenFile.arrayBuffer();
        screenB64 = arrayBufferToBase64(sBytes);
      } catch (sErr) {
        console.warn("Failed to read screen payload:", sErr);
      }
    }

    if (!directText && (!audioFile || !(audioFile instanceof Blob) || audioFile.size < MIN_AUDIO_BYTES)) {
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

    let transcript = directText;
    const audioBytes = audioFile && (audioFile instanceof Blob) ? await audioFile.arrayBuffer() : new Uint8Array(0);
    const tSttStart = performance.now();

    if (!transcript && audioBytes.byteLength >= MIN_AUDIO_BYTES) {
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
    }

    // High-accuracy fallback: Gemini Multimodal Audio transcription
    if (!transcript && geminiApiKey && audioBytes.byteLength >= MIN_AUDIO_BYTES) {
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

    // 6. Fast Real-Time Web Search (Exa) & Wikimedia Encyclopedic Lookup
    let webContext = "";
    let wikiCardData: any = null;

    // Detect if user asks to explain a person or topic
    const topicMatch = transcript.match(/\b(?:explain about|tell me about|who is|who was|biography of|info about|information about|what is|tell me who)\s+([a-zA-Z0-9\s.'"-]+)/i);
    const candidateTopic = topicMatch ? topicMatch[1].replace(/\b(now|today|please|can you|could you)\b/gi, "").trim() : "";

    // 6A. Wikipedia REST API for verified bio & high-res portrait image from Wikimedia Commons
    if (candidateTopic && candidateTopic.length > 2) {
      try {
        const wikiCts = new AbortController();
        const wikiTimer = setTimeout(() => wikiCts.abort(), 2500);
        const wikiResp = await fetch(`https://en.wikipedia.org/api/rest_v1/page/summary/${encodeURIComponent(candidateTopic.replace(/\s+/g, "_"))}`, {
          signal: wikiCts.signal,
          headers: { "User-Agent": "Mozilla/5.0 (compatible; FlinchDesktop/1.0)" }
        });
        clearTimeout(wikiTimer);
        if (wikiResp.ok) {
          const wJson = await wikiResp.json();
          if (wJson.type !== "disambiguation" && wJson.extract) {
            wikiCardData = {
              title: wJson.title,
              description: wJson.description || "",
              extract: wJson.extract,
              thumbnail: wJson.thumbnail?.source || wJson.originalimage?.source || null
            };
          }
        }
      } catch (_wErr) {}
    }

    // 6B. Exa Live Web Search (Aggressive triggers: people, recent events, time, weather, prices, news)
    const isExaTrigger = isLiveQuery(transcript) ||
      /\b(who is|who was|who founded|explain about|tell me about|what time|time in|weather in|how old|net worth|news|latest|update|current|today|status of)\b/i.test(transcript);

    if (exaApiKey && isExaTrigger) {
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
            numResults: 5,
            type: "auto",
            contents: { text: { maxCharacters: 600 } },
          }),
        });
        clearTimeout(exaTimer);
        if (exaResp.ok) {
          const exaJson = await exaResp.json();
          const snippets = (exaJson.results || [])
            .map((r: any) => `${r.title ? `[${r.title}]: ` : ""}${r.text}`)
            .filter((t: any) => typeof t === "string" && t.trim().length > 0)
            .join("\n");
          if (snippets) {
            webContext = snippets.slice(0, 1500);
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

      // If user is inquiring about yesterday's chat memory specifically, fetch yesterday's turns
      if (/\b(yesterday|previous day|last session)\b/i.test(transcript)) {
        try {
          const now = new Date();
          const yesterdayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1, 0, 0, 0).toISOString();
          const yesterdayEnd = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1, 23, 59, 59).toISOString();
          const { data: yHistory } = await adminClient
            .from("chat_messages")
            .select("role, content, created_at")
            .eq("user_id", user.id)
            .gte("created_at", yesterdayStart)
            .lte("created_at", yesterdayEnd)
            .order("created_at", { ascending: true })
            .limit(20);
          if (yHistory && yHistory.length > 0) {
            historyMessages = yHistory.map((m: any) => ({
              role: m.role === "assistant" ? "assistant" : "user",
              content: String(m.content || "").trim(),
            }));
          }
        } catch (_yErr) {}
      }
    } catch (histErr) {
      console.warn("Failed to read conversation history:", histErr);
    }

    // 7.1 Check for PC control / task action intents & Timers/Reminders
    let actionObj: any = null;
    let sceneObj: any = null;
    let reply = "";

    const detectedAction = parseFlinchAction(transcript, uiaText);
    if (detectedAction) {
      actionObj = detectedAction.action;
      reply = detectedAction.reply;
      if (detectedAction.scene) {
        sceneObj = detectedAction.scene;
      }
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
        `You are Flinch—a sharp, mature, friendly, and engaging human tutor and peer.
The user's spoken request: "${transcript}".${historySection}
Look carefully at the attached screenshot of the user's active window/display.${uiaSection}

Instructions:
1. PERSONA: Speak naturally like a smart, warm friend or tutor. NEVER mention that you are "here to help with your screen", NEVER say "I can see on your screen", and NEVER act robotic.
2. STEP-BY-STEP TUTORING RULE (CRITICAL): When the user asks to solve, explain, debug, or fix ANY problem, equation, code error, or concept, NEVER just blurt out the straight final answer first! Walk them through the reasoning step by step (Step 1 -> Step 2 -> Step 3 -> Conclusion) so they genuinely understand how and why it works.
3. PINPOINT VISUAL GROUNDING (box_2d [ymin, xmin, ymax, xmax] 0..1000):
   - Whenever you explain or solve something on screen, generate sequential hand-drawn scene objects ("circle", "highlight", "arrow") pointing to the EXACT sub-expressions or characters of the problem (e.g. tightly wrap ONLY "(1+2)" for Step 1, then tightly wrap ONLY "6 ÷ 2" for Step 2).
   - For maximum spatial accuracy, provide "box_2d": [ymin, xmin, ymax, xmax] in integer coordinates from 0 to 1000 tightly bounding the exact characters/element on the screenshot!
   - Number each step sequentially ("step": 1, "step": 2, "step": 3...) so each shape draws live while you speak that step!
   - ALWAYS include a "notepad" whiteboard object containing a clear numbered step-by-step guide separated by newlines (e.g. "1. First step...\\n2. Second step...\\n3. Third step...\\nResult: ...") so your live pencil writes each step onto the whiteboard synchronized with your voice!

Set durationMs: 25000 when generating visual scene objects.

Supported scene object types:
- "circle": {"type": "circle", "box_2d": [350, 450, 430, 560], "step": 1, "label": "Step 1: (1+2) = 3", "color": "#E11D48"}
- "highlight": {"type": "highlight", "box_2d": [350, 320, 430, 445], "step": 2, "label": "Step 2: 6 ÷ 2 = 3", "color": "#D97706"}
- "arrow": {"type": "arrow", "from": {"x": 0.38, "y": 0.34}, "to": {"x": 0.50, "y": 0.34}, "step": 3, "label": "Step 3: 3 × 3 = 9", "color": "#2563EB"}
- "outline": {"type": "outline", "box_2d": [300, 400, 380, 600], "step": 1, "label": "Target"}
- "callout": {"type": "callout", "box_2d": [400, 400, 500, 700], "text": "Explanation note"}
- "notepad": {"type": "notepad", "title": "Step-by-Step Guide", "text": "1. First step...\\n2. Second step...\\n3. Third step...\\nResult: Final takeaway", "altAnswer": "", "altExplanation": ""}

Return your output strictly as a JSON object:
{
  "answer": "Spoken step-by-step explanation here (no markdown or asterisks)",
  "scene": {
    "objects": [ ... ],
    "durationMs": 25000
  }
}
If no visual overlay is needed, set "scene": null.`;

      // 1. High-Precision Vision via Google Gemini Flash (Multi-tier resilient fallback)
      if (geminiApiKey) {
        const candidateVisionModels = [
          "gemini-3.5-flash",
          "gemini-3.5-flash-lite",
          "gemini-3.6-flash",
          "gemini-2.5-flash",
          "gemini-3-flash-preview"
        ];

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
      const currentUtc = new Date().toUTCString();
      const currentYear = 2026;
      const systemPrompt =
        `You are Flinch—a sharp, mature, friendly, and grounded human peer and tutor.\n` +
        `Current Date & Time: ${currentUtc} (Current Year: ${currentYear}).\n` +
        `1. PERSONA & TONE: Speak naturally like an intelligent, capable friend or colleague. NEVER act dumb, childish, or robotic. NEVER mention that you are "here to help with your screen", NEVER say "I have screen awareness" or "as your desktop AI companion", and NEVER list your features unless explicitly asked what you can do.\n` +
        `2. STEP-BY-STEP TUTORING: When the user asks how to solve, fix, or understand a problem, math question, or technical issue, do NOT just blurt out the straight final answer first. Walk them through it step by step (First... Next... Finally...) like an engaging human tutor so they understand the process before reaching the answer.\n` +
        `3. GREETINGS: If the user greets you (e.g. 'Hey Flinch', 'Hello', 'Hi', 'What's up'), reply warmly and naturally like a peer (e.g. 'Hey, how's it going?' or 'Hey! What's on your mind?') without mentioning their screen or offering generic AI assistance.\n` +
        `4. TIMEZONES: For time queries across different cities or timezones (e.g. UTC, EST, PST, GMT, JST), calculate and state the exact current local time based strictly on the UTC time above.\n` +
        `5. LIVE KNOWLEDGE: For real-time questions, you have live internet access via Exa web search. State fresh, accurate facts as of ${currentYear}. NEVER claim your knowledge is outdated or cut off in the past.\n` +
        `6. VOICE CONCISENESS: Keep spoken responses natural for the ear (1 to 3 crisp sentences). Never use markdown, bullet points, asterisks, or filler.\n` +
        `7. CONVERSATIONAL MEMORY: Use the recent conversation history accurately when the user follows up or refers to earlier turns.`;

      const messages: any[] = [{ role: "system", content: systemPrompt }];
      if (wikiCardData) {
        messages.push({
          role: "system",
          content: `VERIFIED ENCYCLOPEDIC PROFILE FOR "${wikiCardData.title}":\nDescription: ${wikiCardData.description}\nKey Facts: ${wikiCardData.extract}\nProvide a direct, inspiring 1-2 sentence spoken summary answering the user's question about ${wikiCardData.title}. State who they are, their birth/origin, and main companies/inventions clearly.`,
        });
      }
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

    // Ensure any viral 6 ÷ 2(1+2) math query without a whiteboard gets a clean notepad card (without overriding vision coordinates!)
    const lowTx = transcript.toLowerCase();
    const lowReply = reply.toLowerCase();
    const isViralMath = lowTx.includes("6 ÷ 2") || lowTx.includes("6/2") || lowTx.includes("1+2") || lowReply.includes("6 ÷ 2") || lowReply.includes("6/2(1+2)");

    if (isViralMath) {
      if (!sceneObj) {
        sceneObj = {
          durationMs: 25000,
          objects: [
            {
              type: "notepad",
              title: "Order of Operations: 6 ÷ 2(1+2)",
              text: "1. Parentheses First: (1 + 2) = 3  ->  6 ÷ 2(3)\n2. Left-to-Right: 6 ÷ 2 = 3  ->  3(3)\n3. Multiply: 3 x 3 = 9\nResult: 9 (Modern Standard)",
              altAnswer: "1",
              altExplanation: "People get 1 because historical 1917 Cajori conventions (and some calculators) bind implied multiplication 2(3) tighter than division: 6 ÷ [2(3)] = 6 ÷ 6 = 1.\n\nUnder modern PEMDAS / ISO 80000-2 standards, multiplication and division share equal priority left-to-right, giving 9.",
              delayMs: 300
            }
          ]
        };
      }
      if (!reply || reply.length < 10) {
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

    // Visual scene guarantee: if user asked to highlight/circle/point or answer claims something is highlighted
    const isHighlightOrDraw = /\b(highlight|circle|point to|point at|arrow|draw|where is|show me|box|outline|spotlight)\b/i.test(transcript) ||
      /\b(highlighted|circled|pointed to|drawn on screen)\b/i.test(reply);

    if (isHighlightOrDraw && (!sceneObj || !sceneObj.objects || sceneObj.objects.length === 0)) {
      sceneObj = generateFallbackVisualScene(transcript, uiaText, reply);
    }

    // Visual explainer / Bio card guarantee: if topic explanation was fetched with Wikimedia
    if (wikiCardData && (!sceneObj || !sceneObj.objects || sceneObj.objects.length === 0)) {
      sceneObj = {
        durationMs: 25000,
        objects: [
          {
            type: "notepad",
            title: wikiCardData.title,
            subtitle: wikiCardData.description,
            imageUrl: wikiCardData.thumbnail,
            text: formatBioPoints(wikiCardData.extract, wikiCardData.description),
            color: "#3DDC84",
            delayMs: 150
          }
        ]
      };
    }

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
  let low = (voice || "troy").toLowerCase().trim();

  // Voice and emotion picker per mood:
  // Orpheus for casual chat, Deepgram for crisp reading, switched automatically!
  if (low === "auto") {
    const isTechnicalOrReading = text.length > 150 || /\b(error|code|function|class|file|stack trace|traceback|line \d+|step \d+|calculate|according to|summary|chapter|paragraph)\b/i.test(text);
    low = isTechnicalOrReading ? "zeus" : "troy";
  }

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
  type:
    | "open_app"
    | "open_url"
    | "lock_pc"
    | "timer"
    | "reminder"
    | "close_app"
    | "delete_file"
    | "run_command"
    | "system_power"
    | "click_element"
    | "type_text"
    | "press_shortcut";
  target?: string;
  name?: string;
  countdown_sec?: number;
  seconds?: number;
  text?: string;
  label?: string;
  requires_approval?: boolean;
  description?: string;
  command?: string;
  x?: number;
  y?: number;
}

function findUiaTargetCoords(queryName: string, uiaText?: string): { x: number; y: number; w: number; h: number; matchedName: string } | null {
  if (!uiaText || !queryName) return null;
  const qLow = queryName.toLowerCase().trim();
  if (qLow.length < 2) return null;

  try {
    const parsed = JSON.parse(uiaText);
    if (Array.isArray(parsed)) {
      for (const el of parsed) {
        const elName = String(el?.name || "").toLowerCase();
        if (elName && (elName.includes(qLow) || qLow.includes(elName))) {
          const x = Math.max(0.01, Math.min(0.98, Number(el.x) || 0.5));
          const y = Math.max(0.01, Math.min(0.98, Number(el.y) || 0.5));
          const w = Math.max(0.02, Math.min(0.5, Number(el.w) || 0.08));
          const h = Math.max(0.02, Math.min(0.5, Number(el.h) || 0.05));
          return { x, y, w, h, matchedName: String(el.name || queryName) };
        }
      }
    }
  } catch {
    const lines = uiaText.split("\n");
    for (const l of lines) {
      if (l.toLowerCase().includes(qLow)) {
        const mCoord = l.match(/\[\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*\]/);
        if (mCoord) {
          return {
            x: Math.max(0.01, Math.min(0.98, parseFloat(mCoord[1]))),
            y: Math.max(0.01, Math.min(0.98, parseFloat(mCoord[2]))),
            w: Math.max(0.02, Math.min(0.5, parseFloat(mCoord[3]))),
            h: Math.max(0.02, Math.min(0.5, parseFloat(mCoord[4]))),
            matchedName: queryName
          };
        }
      }
    }
  }
  return null;
}

function parseFlinchAction(transcript: string, uiaText?: string): { action: FlinchAction; reply: string; scene?: any } | null {
  const low = transcript.toLowerCase().trim();

  // 1. Timers
  if (low.includes("timer")) {
    let num = 5;
    let unit = "second";
    const numMatch = low.match(/(\d+)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)/i);
    if (numMatch) {
      const rawVal = parseInt(numMatch[1], 10);
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
    let seconds = 300;
    let reminderText = "check your tasks";
    const timeMatch = low.match(/in\s+(\d+)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)/i);
    let timeLabel = "5 minutes";
    if (timeMatch) {
      const val = parseInt(timeMatch[1], 10);
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

  // 3. SENSITIVE ACTIONS (Require Explicit User Approval)
  // 3A. System Power (Shutdown, Restart, Sleep, Sign Out, Lock)
  if (/\b(shut\s*down|power\s*off|turn\s*off\s+my\s+(?:pc|computer))\b/i.test(low)) {
    return {
      action: {
        type: "system_power",
        target: "shutdown",
        name: "Shut Down PC",
        requires_approval: true,
        description: "Closes all active applications and powers off Windows.",
        command: "shutdown.exe /s /t 5"
      },
      reply: "Please confirm on screen if you'd like me to shut down your PC."
    };
  }
  if (/\b(restart|reboot)\s+(?:my\s+)?(?:pc|computer|system|windows)\b/i.test(low)) {
    return {
      action: {
        type: "system_power",
        target: "restart",
        name: "Restart PC",
        requires_approval: true,
        description: "Closes active sessions and restarts your computer.",
        command: "shutdown.exe /r /t 5"
      },
      reply: "Please confirm if you'd like me to restart your PC."
    };
  }
  if (/\b(put\s+(?:my\s+)?(?:pc|computer)\s+to\s+sleep|sleep\s+mode|hibernate)\b/i.test(low)) {
    return {
      action: {
        type: "system_power",
        target: "sleep",
        name: "Sleep Mode",
        requires_approval: true,
        description: "Suspends active sessions and puts your PC into low-power sleep.",
        command: "rundll32.exe powrprof.dll,SetSuspendState 0,1,0"
      },
      reply: "Please approve on screen to put your PC to sleep."
    };
  }
  if (low.includes("lock my pc") || low.includes("lock pc") || low.includes("lock computer") || low.includes("lock screen")) {
    return {
      action: {
        type: "lock_pc",
        target: "lock",
        name: "Lock Workstation",
        requires_approval: true,
        description: "Locks your desktop session and returns to the Windows lock screen.",
        command: "rundll32.exe user32.dll,LockWorkStation"
      },
      reply: "Please confirm if you want me to lock your workstation."
    };
  }

  // 3B. Close / Kill Applications (Sensitive: unsaved work risk)
  const closeMatch = low.match(/\b(?:close|quit|kill|terminate|exit|force\s+close)\s+(?:my\s+|the\s+)?(chrome|google\s+chrome|edge|microsoft\s+edge|browser|notepad|vs\s*code|vscode|spotify|discord|slack|word|excel|powerpoint|task\s+manager|explorer|terminal|powershell)\b/i);
  if (closeMatch) {
    const rawApp = closeMatch[1].toLowerCase();
    let procTarget = rawApp;
    let displayApp = closeMatch[1];
    if (rawApp.includes("chrome")) { procTarget = "chrome"; displayApp = "Google Chrome"; }
    else if (rawApp.includes("edge") || rawApp === "browser") { procTarget = "msedge"; displayApp = "Microsoft Edge"; }
    else if (rawApp.includes("notepad")) { procTarget = "notepad"; displayApp = "Notepad"; }
    else if (rawApp.includes("code")) { procTarget = "Code"; displayApp = "VS Code"; }
    else if (rawApp.includes("spotify")) { procTarget = "Spotify"; displayApp = "Spotify"; }
    else if (rawApp.includes("discord")) { procTarget = "Discord"; displayApp = "Discord"; }
    else if (rawApp.includes("slack")) { procTarget = "slack"; displayApp = "Slack"; }
    else if (rawApp.includes("word")) { procTarget = "WINWORD"; displayApp = "Microsoft Word"; }
    else if (rawApp.includes("excel")) { procTarget = "EXCEL"; displayApp = "Microsoft Excel"; }
    else if (rawApp.includes("powerpoint")) { procTarget = "POWERPNT"; displayApp = "PowerPoint"; }
    else if (rawApp.includes("task")) { procTarget = "Taskmgr"; displayApp = "Task Manager"; }
    else if (rawApp.includes("terminal") || rawApp.includes("powershell")) { procTarget = "WindowsTerminal"; displayApp = "Terminal"; }

    return {
      action: {
        type: "close_app",
        target: procTarget,
        name: `Close ${displayApp}`,
        requires_approval: true,
        description: `Terminates running ${displayApp} windows. Unsaved changes in ${displayApp} may be lost.`,
        command: `taskkill /IM ${procTarget}.exe`
      },
      reply: `Closing ${displayApp} requires your approval so you don't lose unsaved work.`
    };
  }

  // 3C. Delete / Clear Files or Recycle Bin (Sensitive: data deletion risk)
  if (/\b(empty\s+(?:the\s+)?recycle\s+bin|clear\s+(?:the\s+)?recycle\s+bin|empty\s+trash)\b/i.test(low)) {
    return {
      action: {
        type: "run_command",
        target: "Clear-RecycleBin",
        name: "Empty Recycle Bin",
        requires_approval: true,
        description: "Permanently deletes all items currently stored in the Windows Recycle Bin.",
        command: "Clear-RecycleBin -Force -ErrorAction SilentlyContinue"
      },
      reply: "Emptying the Recycle Bin permanently deletes files. Please approve this action."
    };
  }
  const deleteFileMatch = low.match(/\b(?:delete|remove|erase|clean\s+up)\s+(?:my\s+|the\s+|all\s+)?(temp\s+files|temporary\s+files|downloads\s+folder|screenshots?|desktop\s+files|[a-z0-9_\-\.]+\.(?:txt|png|jpg|pdf|zip|docx|html))\b/i);
  if (deleteFileMatch) {
    const targetItem = deleteFileMatch[1].trim();
    return {
      action: {
        type: "delete_file",
        target: targetItem,
        name: `Delete ${targetItem}`,
        requires_approval: true,
        description: `Removes ${targetItem} from your local disk. Please verify before proceeding.`,
        command: `Remove-Item "${targetItem}"`
      },
      reply: `Deleting ${targetItem} is a sensitive file operation. Please approve on screen.`
    };
  }

  // 3D. System / Network Shell Commands (Sensitive)
  if (/\b(flush\s+dns|reset\s+network|clear\s+dns\s+cache)\b/i.test(low)) {
    return {
      action: {
        type: "run_command",
        target: "ipconfig /flushdns",
        name: "Flush DNS Resolver Cache",
        requires_approval: true,
        description: "Executes ipconfig /flushdns to clear and reset the Windows DNS resolver cache.",
        command: "ipconfig /flushdns"
      },
      reply: "I can flush your DNS cache. Please approve the system command on screen."
    };
  }

  // 4. PC CONTROL: Mouse Clicks, Typing, & Keyboard Shortcuts
  const clickMatch = low.match(/\b(?:click|press|tap)\s+(?:on\s+)?(?:the\s+)?([a-z0-9 _\-]+?)(?:\s+button|\s+icon|\s+link|\s+tab)?$/i);
  if (clickMatch && !low.includes("how to") && !low.includes("where is")) {
    const targetLabel = clickMatch[1].replace(/\b(on|my|screen|window|please|now)\b/gi, "").trim();
    if (targetLabel.length >= 2) {
      const isSensitiveClick = /\b(delete|remove|submit|send|pay|buy|purchase|checkout|format|erase|uninstall|confirm|sign\s*out|log\s*out)\b/i.test(targetLabel);
      const uiaHit = findUiaTargetCoords(targetLabel, uiaText);
      const cx = uiaHit ? Math.round((uiaHit.x + uiaHit.w / 2) * 1000) / 1000 : 0.5;
      const cy = uiaHit ? Math.round((uiaHit.y + uiaHit.h / 2) * 1000) / 1000 : 0.5;

      const clickScene = uiaHit ? {
        durationMs: 6000,
        objects: [
          {
            type: "circle",
            rect: { x: uiaHit.x, y: uiaHit.y, w: uiaHit.w, h: uiaHit.h },
            label: `Click: ${uiaHit.matchedName}`,
            color: "#0284C7",
            step: 1,
            delayMs: 0
          }
        ]
      } : undefined;

      return {
        action: {
          type: "click_element",
          target: uiaHit ? uiaHit.matchedName : targetLabel,
          name: `Click "${uiaHit ? uiaHit.matchedName : targetLabel}"`,
          x: cx,
          y: cy,
          requires_approval: isSensitiveClick,
          description: isSensitiveClick
            ? `Clicks the "${targetLabel}" control on your active window.`
            : `Moves cursor and clicks "${targetLabel}".`,
          command: `MouseClick(${Math.round(cx * 100)}%, ${Math.round(cy * 100)}%)`
        },
        reply: isSensitiveClick
          ? `Clicking ${targetLabel} is a sensitive action. Please confirm on screen.`
          : `Clicking ${uiaHit ? uiaHit.matchedName : targetLabel} now.`,
        scene: clickScene
      };
    }
  }

  // 4B. Type Text on Keyboard
  const typeMatch = transcript.match(/\b(?:type|enter|input|write\s+out)\s+["']?(.+?)["']?$/i);
  if (typeMatch && !low.includes("essay") && !low.includes("report") && !low.includes("presentation") && !low.includes("article")) {
    const textToType = typeMatch[1].trim();
    if (textToType.length > 0 && textToType.length < 200) {
      return {
        action: {
          type: "type_text",
          text: textToType,
          name: `Type "${textToType.slice(0, 28)}"`,
          requires_approval: false,
          countdown_sec: 0,
          command: `SendKeys("${textToType.slice(0, 40)}")`
        },
        reply: `Typing "${textToType}" into your active window.`
      };
    }
  }

  // 4C. Keyboard Shortcuts / Hotkeys (Copy, Paste, Undo, Save, Select All, Minimize, Switch Window, Screenshot)
  if (/\b(press\s+enter|hit\s+enter|submit\s+form)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "{ENTER}", name: "Press Enter", requires_approval: true, description: "Sends the Enter key to the active window, which may submit a form or message.", command: "SendKeys({ENTER})" },
      reply: "Please confirm before I press Enter in your active window."
    };
  }
  if (/\b(save\s+this\s+file|press\s+ctrl\s*\+?\s*s|save\s+document)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "^s", name: "Save File (Ctrl+S)", requires_approval: false, command: "SendKeys(Ctrl+S)" },
      reply: "Saving your active file now."
    };
  }
  if (/\b(undo\s+that|press\s+ctrl\s*\+?\s*z)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "^z", name: "Undo (Ctrl+Z)", requires_approval: false, command: "SendKeys(Ctrl+Z)" },
      reply: "Undoing the last action."
    };
  }
  if (/\b(select\s+all|press\s+ctrl\s*\+?\s*a)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "^a", name: "Select All (Ctrl+A)", requires_approval: false, command: "SendKeys(Ctrl+A)" },
      reply: "Selecting all content."
    };
  }
  if (/\b(paste\s+that|paste\s+clipboard|press\s+ctrl\s*\+?\s*v)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "^v", name: "Paste (Ctrl+V)", requires_approval: false, command: "SendKeys(Ctrl+V)" },
      reply: "Pasting from your clipboard."
    };
  }
  if (/\b(new\s+tab|open\s+a\s+new\s+tab)\b/i.test(low)) {
    return {
      action: { type: "press_shortcut", target: "^t", name: "New Tab (Ctrl+T)", requires_approval: false, command: "SendKeys(Ctrl+T)" },
      reply: "Opening a new browser tab."
    };
  }
  if (/\b(close\s+this\s+tab|close\s+current\s+tab)\b/i.test(low)) {
    return {
      action: {
        type: "press_shortcut",
        target: "^w",
        name: "Close Current Tab (Ctrl+W)",
        requires_approval: true,
        description: "Closes the active tab in your current application.",
        command: "SendKeys(Ctrl+W)"
      },
      reply: "Please confirm if you'd like me to close your active tab."
    };
  }

  // 5. Safe Open Apps
  if (low.includes("spotify")) {
    return { action: { type: "open_app", target: "spotify", name: "Spotify", countdown_sec: 3 }, reply: "Opening Spotify." };
  }
  if (low.includes("chrome") || low.includes("browser") || low.includes("google chrome")) {
    return { action: { type: "open_app", target: "chrome", name: "Google Chrome", countdown_sec: 3 }, reply: "Opening Google Chrome." };
  }
  if (low.includes("vs code") || low.includes("vscode") || low.includes("open code")) {
    return { action: { type: "open_app", target: "code", name: "VS Code", countdown_sec: 3 }, reply: "Opening VS Code." };
  }
  if (low.includes("notepad")) {
    return { action: { type: "open_app", target: "notepad", name: "Notepad", countdown_sec: 3 }, reply: "Opening Notepad." };
  }
  if (low.includes("calculator") || low.includes("calc")) {
    return { action: { type: "open_app", target: "calc", name: "Calculator", countdown_sec: 3 }, reply: "Opening Calculator." };
  }
  if (low.includes("terminal") || low.includes("powershell") || low.includes("command prompt")) {
    return { action: { type: "open_app", target: "terminal", name: "Terminal", countdown_sec: 3 }, reply: "Opening Terminal." };
  }
  if (low.includes("file explorer") || low.includes("my files") || low.includes("open files")) {
    return { action: { type: "open_app", target: "explorer", name: "File Explorer", countdown_sec: 3 }, reply: "Opening File Explorer." };
  }
  if (low.includes("task manager")) {
    return { action: { type: "open_app", target: "taskmgr", name: "Task Manager", countdown_sec: 3 }, reply: "Opening Task Manager." };
  }
  if (low.includes("settings") || low.includes("pc settings")) {
    return { action: { type: "open_app", target: "settings", name: "Windows Settings", countdown_sec: 3 }, reply: "Opening Settings." };
  }

  // 6. Safe Open URLs
  if (low.includes("youtube")) {
    return { action: { type: "open_url", target: "https://youtube.com", name: "YouTube", countdown_sec: 3 }, reply: "Opening YouTube." };
  }
  if (low.includes("github")) {
    return { action: { type: "open_url", target: "https://github.com", name: "GitHub", countdown_sec: 3 }, reply: "Opening GitHub." };
  }
  if (low.includes("twitter") || low.includes("open x")) {
    return { action: { type: "open_url", target: "https://x.com", name: "X", countdown_sec: 3 }, reply: "Opening X." };
  }
  if (low.includes("reddit")) {
    return { action: { type: "open_url", target: "https://reddit.com", name: "Reddit", countdown_sec: 3 }, reply: "Opening Reddit." };
  }
  if (low.includes("gmail") || low.includes("my email")) {
    return { action: { type: "open_url", target: "https://mail.google.com", name: "Gmail", countdown_sec: 3 }, reply: "Opening Gmail." };
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
    // People & Topics
    "who is", "who was", "who founded", "who invented", "explain about", "tell me about",
    "biography", "how old is", "where was", "when was", "net worth", "what did", "status of",
    // World Clock & Timezones
    "what time", "current time", "time in", "time now", "clock in", "timezone",
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
    "search", "look up", "google", "find out", "check the web", "browse", "who is the current", "what is the current"
  ];
  return triggers.some((t) => low.includes(t));
}

function formatBioPoints(extract: string, desc: string): string {
  const points: string[] = [];
  if (desc) points.push(`Role: ${desc}`);
  const sents = (extract || "").split(/(?<=[.?!])\s+/).filter((s) => s.trim().length > 10);
  for (let i = 0; i < Math.min(3, sents.length); i++) {
    points.push(sents[i].trim());
  }
  return points.join("\n\n");
}

// Screen awareness query classifier (read screen, explain error, look at screen, point out, highlight)
function isScreenAwarenessQuery(q: string): boolean {
  const low = q.toLowerCase();
  const triggers = [
    "screen", "look at this", "look here", "what am i looking at", "what's this on", "what is this on",
    "explain this error", "read this", "read my screen", "read the text", "what does this error",
    "explain the error", "what does this say", "summarize this page",
    "summarize this window", "can you see", "do you see", "on my display", "on the monitor",
    "point to", "highlight", "draw on", "circle the", "circle this", "arrow to", "where is the", "show me on", "which button",
    "how do i fix this", "solve this", "can you solve this",
    "solve the problem on", "check the screen", "on the screen", "on screen",
    "6 ÷ 2", "6/2"
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

// Generates a guaranteed visual overlay targeting requested element when vision model omits it
function generateFallbackVisualScene(transcript: string, uiaText: string | undefined, reply: string): any {
  let label = "Target";
  let rect = { x: 0.40, y: 0.38, w: 0.20, h: 0.16 };
  let found = false;

  const m = transcript.match(/\b(?:highlight|circle|point to|point at|draw|where is|show me)\s+(?:the\s+)?([a-z0-9 _\-]+)/i);
  if (m && m[1]) {
    label = m[1].replace(/\b(on|my|the|screen|desktop|display|window)\b/gi, "").trim();
    if (!label) label = "Target";
  }

  if (uiaText) {
    const lines = uiaText.split("\n");
    for (const l of lines) {
      if (label && label.length > 2 && l.toLowerCase().includes(label.toLowerCase())) {
        const mCoord = l.match(/\[\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*\]/);
        if (mCoord) {
          const x = Math.max(0, Math.min(0.95, parseFloat(mCoord[1])));
          const y = Math.max(0, Math.min(0.95, parseFloat(mCoord[2])));
          const w = Math.max(0.02, Math.min(0.8, parseFloat(mCoord[3])));
          const h = Math.max(0.02, Math.min(0.8, parseFloat(mCoord[4])));
          rect = { x, y, w, h };
          found = true;
          break;
        }
      }
    }
  }

  if (!found) {
    const lowTarget = label.toLowerCase();
    if (lowTarget.includes("recycle") || lowTarget.includes("bin") || lowTarget.includes("trash")) {
      label = "Recycle Bin";
      rect = { x: 0.02, y: 0.035, w: 0.065, h: 0.085 };
    } else if (lowTarget.includes("taskbar") || lowTarget.includes("start")) {
      label = "Taskbar / Start";
      rect = { x: 0.015, y: 0.94, w: 0.06, h: 0.05 };
    } else if (lowTarget.includes("clock") || lowTarget.includes("time") || lowTarget.includes("date")) {
      label = "System Clock";
      rect = { x: 0.91, y: 0.945, w: 0.08, h: 0.05 };
    }
  }

  const arrowFrom = {
    x: Math.min(0.95, rect.x + rect.w + 0.10),
    y: Math.min(0.95, rect.y + rect.h / 2)
  };
  const arrowTo = {
    x: Math.min(0.95, rect.x + rect.w + 0.01),
    y: Math.min(0.95, rect.y + rect.h / 2)
  };

  return {
    durationMs: 15000,
    objects: [
      {
        type: "highlight",
        rect: rect,
        label: label,
        color: "#FACC15",
        step: 1,
        delayMs: 0
      },
      {
        type: "arrow",
        from: arrowFrom,
        to: arrowTo,
        label: label,
        color: "#3DDC84",
        step: 2,
        delayMs: 500
      }
    ]
  };
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

    const parseBox2D = (b: any): { x: number; y: number; w: number; h: number } | null => {
      if (!Array.isArray(b) || b.length < 4) return null;
      const n0 = parseFloat(b[0]);
      const n1 = parseFloat(b[1]);
      const n2 = parseFloat(b[2]);
      const n3 = parseFloat(b[3]);
      if (isNaN(n0) || isNaN(n1) || isNaN(n2) || isNaN(n3)) return null;
      // Gemini native box_2d is [ymin, xmin, ymax, xmax] in 0..1000 (or 0..1)
      const scale = (n0 > 1.0 || n1 > 1.0 || n2 > 1.0 || n3 > 1.0) ? 1000.0 : 1.0;
      const ymin = clamp(Math.min(n0, n2) / scale);
      const xmin = clamp(Math.min(n1, n3) / scale);
      const ymax = clamp(Math.max(n0, n2) / scale);
      const xmax = clamp(Math.max(n1, n3) / scale);
      const w = Math.max(0.015, xmax - xmin);
      const h = Math.max(0.015, ymax - ymin);
      return {
        x: xmin,
        y: ymin,
        w: Math.min(1.0 - xmin, w),
        h: Math.min(1.0 - ymin, h),
      };
    };

    const boxRect = parseBox2D(obj.box_2d || obj.box2d || obj.bbox);

    const cleanObj: any = { type };

    if (type === "notepad" || type === "notebook") {
      cleanObj.title = String(obj.title || "Topic Overview").slice(0, 150);
      cleanObj.text = String(obj.text || "").slice(0, 1000);
      if (obj.subtitle) cleanObj.subtitle = String(obj.subtitle).slice(0, 200);
      if (obj.imageUrl) cleanObj.imageUrl = String(obj.imageUrl).slice(0, 500);
      if (obj.altAnswer) cleanObj.altAnswer = String(obj.altAnswer).slice(0, 50);
      if (obj.altExplanation) cleanObj.altExplanation = String(obj.altExplanation).slice(0, 600);
      if (boxRect) {
        cleanObj.rect = boxRect;
      } else if (obj.rect) {
        cleanObj.rect = { x: clamp(obj.rect.x), y: clamp(obj.rect.y), w: clamp(obj.rect.w), h: clamp(obj.rect.h) };
      }
      if (obj.color) cleanObj.color = String(obj.color).trim();
      if (typeof obj.delayMs === "number") cleanObj.delayMs = Math.max(0, Math.min(15000, obj.delayMs));
    }
    else if (type === "arrow") {
      if (!obj.from || !obj.to) continue;
      const normCoord = (v: any) => {
        const num = typeof v === "number" ? v : parseFloat(v);
        if (isNaN(num)) return 0;
        return clamp(num > 1.0 ? num / 1000.0 : num);
      };
      cleanObj.from = { x: normCoord(obj.from.x), y: normCoord(obj.from.y) };
      cleanObj.to = { x: normCoord(obj.to.x), y: normCoord(obj.to.y) };
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
      if (boxRect) {
        cleanObj.rect = boxRect;
      } else if (obj.rect) {
        const rx = clamp(obj.rect.x > 1.0 ? obj.rect.x / 1000.0 : obj.rect.x);
        const ry = clamp(obj.rect.y > 1.0 ? obj.rect.y / 1000.0 : obj.rect.y);
        const rawW = obj.rect.w || obj.rect.width || 0.1;
        const rawH = obj.rect.h || obj.rect.height || 0.1;
        const rw = Math.max(0.015, clamp(rawW > 1.0 ? rawW / 1000.0 : rawW));
        const rh = Math.max(0.015, clamp(rawH > 1.0 ? rawH / 1000.0 : rawH));
        cleanObj.rect = { x: rx, y: ry, w: Math.min(1.0 - rx, rw), h: Math.min(1.0 - ry, rh) };
      } else if (obj.from) {
        cleanObj.from = {
          x: clamp(obj.from.x > 1.0 ? obj.from.x / 1000.0 : obj.from.x),
          y: clamp(obj.from.y > 1.0 ? obj.from.y / 1000.0 : obj.from.y),
        };
        if (typeof obj.radius === "number") cleanObj.radius = Math.max(0.005, Math.min(0.5, obj.radius));
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
      if (boxRect) {
        cleanObj.rect = boxRect;
      } else {
        const rect = obj.rect || {};
        const rx = clamp(rect.x > 1.0 ? rect.x / 1000.0 : rect.x);
        const ry = clamp(rect.y > 1.0 ? rect.y / 1000.0 : rect.y);
        const rawW = rect.w || rect.width || 0.1;
        const rawH = rect.h || rect.height || 0.05;
        const rw = Math.max(0.015, clamp(rawW > 1.0 ? rawW / 1000.0 : rawW));
        const rh = Math.max(0.015, clamp(rawH > 1.0 ? rawH / 1000.0 : rawH));
        cleanObj.rect = {
          x: rx,
          y: ry,
          w: Math.min(1.0 - rx, rw),
          h: Math.min(1.0 - ry, rh),
        };
      }

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

  let durationMs = 15000;
  if (typeof sceneRaw.durationMs === "number" && sceneRaw.durationMs >= 2000 && sceneRaw.durationMs <= 45000) {
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
              "You are a factual knowledge extractor. Extract any permanent personal facts, preferences, names, locations, or project details stated by the user. If the user is only asking a question, making small talk, or no permanent fact is stated, reply exactly 'NONE'. If there is a fact, reply with 1 short declarative bullet (e.g. 'User prefers dark mode'). Do not add any markdown, asterisks, or extra explanation.",
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

function escapeHtml(str: string): string {
  return String(str || "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function escapeJs(str: string): string {
  return String(str || "").replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n");
}

function buildHtmlPresentation(topic: string, imageUrl: string, deck: any): string {
  const title = deck?.title || `Presentation: ${topic}`;
  const slides = Array.isArray(deck?.slides) && deck.slides.length > 0 ? deck.slides : [
    { heading: topic, bullets: ["Key milestones and background", "Core achievements", "Impact and legacy"], voiceover: `Welcome to this presentation on ${topic}. Let us explore the key background, achievements, and future impact.` }
  ];

  const slidesJson = JSON.stringify(slides).replace(/</g, "\\u003c");

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>${escapeHtml(title)}</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: #f8fafc;
    color: #0f172a;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    min-height: 100vh;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  header {
    padding: 16px 36px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid #e2e8f0;
    background: #ffffff;
  }
  .deck-title {
    font-size: 15px;
    font-weight: 600;
    color: #0f172a;
    letter-spacing: -0.01em;
  }
  
  main {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 40px;
    position: relative;
    background: #f1f5f9;
  }
  .slide-card {
    width: 100%;
    max-width: 960px;
    min-height: 520px;
    background: #ffffff;
    border: 1px solid #e2e8f0;
    border-radius: 16px;
    padding: 48px 56px;
    box-shadow: 0 10px 30px -10px rgba(0, 0, 0, 0.08), 0 4px 6px -2px rgba(0, 0, 0, 0.03);
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    animation: fadeIn 0.25s ease;
  }
  @keyframes fadeIn { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: translateY(0); } }
  
  .slide-header {
    display: flex;
    justify-content: space-between;
    gap: 32px;
    align-items: flex-start;
  }
  .slide-heading {
    font-size: 34px;
    font-weight: 700;
    line-height: 1.25;
    color: #0f172a;
    letter-spacing: -0.02em;
    margin-bottom: 24px;
  }
  .topic-img {
    width: 130px;
    height: 130px;
    border-radius: 14px;
    object-fit: cover;
    border: 1px solid #cbd5e1;
    box-shadow: 0 4px 12px rgba(0,0,0,0.08);
    flex-shrink: 0;
  }
  
  .bullets {
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: 16px;
    margin: 12px 0 28px 0;
  }
  .bullets li {
    font-size: 19px;
    line-height: 1.55;
    color: #334155;
    display: flex;
    align-items: flex-start;
    gap: 14px;
  }
  .bullets li::before {
    content: "•";
    color: #0284c7;
    font-size: 24px;
    line-height: 1;
    margin-top: 2px;
  }
  
  .voice-banner {
    background: #f8fafc;
    border-left: 3px solid #0284c7;
    border-radius: 8px;
    padding: 14px 20px;
    display: flex;
    align-items: center;
    gap: 14px;
    margin-top: auto;
  }
  .voice-indicator {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: #0284c7;
    box-shadow: 0 0 8px #0284c7;
    animation: pulse 1.5s infinite;
    flex-shrink: 0;
  }
  @keyframes pulse { 0%, 100% { opacity: 0.3; } 50% { opacity: 1; } }
  .voice-text {
    font-size: 14px;
    color: #64748b;
    font-style: italic;
    line-height: 1.45;
    flex: 1;
  }
  
  footer {
    padding: 16px 36px;
    background: #ffffff;
    border-top: 1px solid #e2e8f0;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 20px;
  }
  .controls {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  button {
    background: #ffffff;
    border: 1px solid #cbd5e1;
    color: #1e293b;
    padding: 9px 18px;
    border-radius: 8px;
    font-size: 13px;
    font-weight: 600;
    cursor: pointer;
    transition: all 0.15s ease;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  button:hover {
    background: #f1f5f9;
    border-color: #94a3b8;
  }
  button.primary {
    background: #0f172a;
    color: #ffffff;
    border-color: #0f172a;
  }
  button.primary:hover {
    background: #1e293b;
  }
  .progress-wrap {
    display: flex;
    align-items: center;
    gap: 14px;
    font-size: 13px;
    color: #64748b;
  }
  .progress-bar {
    width: 140px;
    height: 6px;
    border-radius: 3px;
    background: #e2e8f0;
    overflow: hidden;
  }
  .progress-fill {
    height: 100%;
    background: #0284c7;
    transition: width 0.25s ease;
  }
</style>
</head>
<body>
<header>
  <div class="deck-title">${escapeHtml(title)}</div>
</header>
<main>
  <div class="slide-card" id="slideCard"></div>
</main>
<footer>
  <div class="controls">
    <button onclick="prevSlide()">◄ Previous</button>
    <button onclick="nextSlide()">Next ►</button>
    <button class="primary" id="voiceBtn" onclick="toggleVoice()">🔊 Play Voiceover</button>
  </div>
  <div class="progress-wrap">
    <label style="display:flex;align-items:center;gap:6px;cursor:pointer;">
      <input type="checkbox" id="autoVoice" checked> Auto-Narrate
    </label>
    <div class="progress-bar"><div class="progress-fill" id="progressFill"></div></div>
    <span id="slideCounter">Slide 1 of 6</span>
  </div>
</footer>
<script>
  const slides = ${slidesJson};
  const topicImage = "${escapeJs(imageUrl)}";
  let cur = 0;
  let isSpeaking = false;

  function render() {
    const s = slides[cur];
    const card = document.getElementById("slideCard");
    card.innerHTML = "";

    const contentDiv = document.createElement("div");

    const headerDiv = document.createElement("div");
    headerDiv.className = "slide-header";

    const heading = document.createElement("h1");
    heading.className = "slide-heading";
    heading.textContent = s.heading || "";
    headerDiv.appendChild(heading);

    if (cur === 0 && topicImage) {
      const img = document.createElement("img");
      img.className = "topic-img";
      img.src = topicImage;
      img.alt = "Topic";
      img.onerror = function() { this.style.display = "none"; };
      headerDiv.appendChild(img);
    }
    contentDiv.appendChild(headerDiv);

    const ul = document.createElement("ul");
    ul.className = "bullets";
    (s.bullets || []).forEach(function(b) {
      const li = document.createElement("li");
      li.textContent = b;
      ul.appendChild(li);
    });
    contentDiv.appendChild(ul);
    card.appendChild(contentDiv);

    if (s.voiceover) {
      const voiceBanner = document.createElement("div");
      voiceBanner.className = "voice-banner";

      const indicator = document.createElement("div");
      indicator.className = "voice-indicator";
      voiceBanner.appendChild(indicator);

      const voiceText = document.createElement("div");
      voiceText.className = "voice-text";
      voiceText.textContent = '"' + s.voiceover + '"';
      voiceBanner.appendChild(voiceText);

      card.appendChild(voiceBanner);
    }

    document.getElementById("slideCounter").textContent = "Slide " + (cur + 1) + " of " + slides.length;
    document.getElementById("progressFill").style.width = (((cur + 1) / slides.length) * 100) + "%";

    if (document.getElementById("autoVoice").checked) {
      speakCurrent();
    }
  }

  function speakCurrent() {
    if (!window.speechSynthesis) return;
    window.speechSynthesis.cancel();
    const text = slides[cur] && slides[cur].voiceover;
    if (!text) return;
    const u = new SpeechSynthesisUtterance(text);
    u.rate = 1.0;
    u.pitch = 1.0;
    u.onstart = function() { isSpeaking = true; updateVoiceBtn(); };
    u.onend = function() { isSpeaking = false; updateVoiceBtn(); };
    u.onerror = function() { isSpeaking = false; updateVoiceBtn(); };
    window.speechSynthesis.speak(u);
  }

  function toggleVoice() {
    if (!window.speechSynthesis) return;
    if (window.speechSynthesis.speaking) {
      window.speechSynthesis.cancel();
      isSpeaking = false;
      updateVoiceBtn();
    } else {
      speakCurrent();
    }
  }

  function updateVoiceBtn() {
    const btn = document.getElementById("voiceBtn");
    if (!window.speechSynthesis) return;
    btn.innerText = window.speechSynthesis.speaking ? "⏸ Pause Voiceover" : "🔊 Play Voiceover";
  }

  function nextSlide() {
    if (cur < slides.length - 1) { cur++; render(); }
  }
  function prevSlide() {
    if (cur > 0) { cur--; render(); }
  }

  window.addEventListener("keydown", function(e) {
    if (e.key === "ArrowRight" || e.key === " " || e.key === "PageDown") nextSlide();
    else if (e.key === "ArrowLeft" || e.key === "PageUp") prevSlide();
  });

  render();
</script>
</body>
</html>`;
}
