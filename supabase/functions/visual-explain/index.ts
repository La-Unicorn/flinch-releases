// ============================================================================
// FLINCH AI: SUPABASE EDGE FUNCTION (visual-explain)
// Visual Explain: Multimodal Vision + Step-by-Step Whiteboard Animation JSON
// Cost: 4 Quota Units | Atomic Rate-Limiting & Auto-Refund
// Scope (Strict v1): Math problems and simple labeled diagrams only
// ============================================================================

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.39.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Content-Type": "application/json; charset=utf-8",
};

const MAX_PAYLOAD_BYTES = 12 * 1024 * 1024; // 12 MB maximum screenshot size
const EXPLAIN_WEIGHT = 4; // Visual Explain strictly consumes 4 quota units

const SYSTEM_INSTRUCTION = `You are Flinch's Visual Explain math tutor. Your role is to analyze the math problem visible on the user's screen and explain the solution step-by-step by drawing on a digital whiteboard canvas (1000x700 coordinate space) while narrating each step.

SCOPE (STRICT V1): Math problems and simple labeled diagrams only (algebra, arithmetic, geometry, calculus, trigonometry). No physics/chemistry/biology.

COORDINATE SYSTEM (0-1000):
- All drawings use normalized 0-1000 coordinates.
- Whiteboard canvas is 1000 units wide, 700 units tall.
- Left column (x: 80-480) for the main equations, worked steps, or diagrams.
- Right column (x: 520-920) for explanatory step notes (e.g. "\\\\text{Step 1: } ...").
- highlight_on_screen is the ONLY element positioned relative to the user's screenshot (0-1000 bounding box of where the problem is located on their actual screen).

ALLOWED DRAW PRIMITIVES:
- text: { type: "text", text: "LaTeX string", x: number, y: number, size: number, color: string }
- line, arrow: { type: "line"|"arrow", x1: number, y1: number, x2: number, y2: number, color: string }
- circle: { type: "circle", cx: number, cy: number, r: number, color: string }
- rect: { type: "rect", x: number, y: number, w: number, h: number, color: string }
- triangle: { type: "triangle", points: [[x1, y1], [x2, y2], [x3, y3]], color: string }
- right_angle_marker: { type: "right_angle_marker", x: number, y: number, size: number, dirX: 1|-1, dirY: 1|-1, color: string }
- angle_arc: { type: "angle_arc", cx: number, cy: number, r: number, start: number, end: number, label: string, color: string }
- path: { type: "path", points: [[x, y], ...], color: string, strokeWidth: number }
- underline: { type: "underline", x: number, y: number, w: number, color: string }
- highlight: { type: "highlight", x: number, y: number, w: number, h: number, color: string }
- brace: { type: "brace", x: number, y: number, w: number, orientation: "top"|"bottom"|"left"|"right", label: string, color: string }
- point: { type: "point", x: number, y: number, label: string, color: string }
- axes: { type: "axes", originX: number, originY: number, lengthX: number, lengthY: number, color: string }
- number_line: { type: "number_line", x: number, y: number, length: number, min: number, max: number, color: string }
- check: { type: "check", x: number, y: number, size: number, color: string }
- cross: { type: "cross", x: number, y: number, size: number, color: string }
- highlight_on_screen: { type: "highlight_on_screen", x: number, y: number, w: number, h: number, label: string }

STEP RULES:
1. In Step 1, if the problem is visible on the user's screen, include "highlight_on_screen" circling its bounding box in the screenshot.
2. Provide 2 to 4 progressive beats.
3. In each step, provide:
   - "narration": exactly 1 short, natural spoken sentence explaining this beat (suitable for TTS, no LaTeX or markdown).
   - "draw": array of primitives to display.
4. On the final step, frame the final numeric/expression answer with a "rect" and place a "check" beside it.
5. Return ONLY a structured JSON matching the schema.`;

serve(async (req: Request) => {
  // 1. CORS Preflight
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  if (req.method !== "POST") {
    return new Response(JSON.stringify({ error: "method_not_allowed" }), {
      status: 405,
      headers: corsHeaders,
    });
  }

  // 2. Request Size Guard
  const contentLength = parseInt(req.headers.get("content-length") || "0", 10);
  if (contentLength > MAX_PAYLOAD_BYTES) {
    return new Response(
      JSON.stringify({
        error: "payload_too_large",
        message: `Payload exceeds maximum allowed size of ${MAX_PAYLOAD_BYTES} bytes.`,
      }),
      { status: 413, headers: corsHeaders }
    );
  }

  try {
    const supabaseUrl = Deno.env.get("SUPABASE_URL");
    const supabaseAnonKey = Deno.env.get("SUPABASE_ANON_KEY");
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
    const geminiApiKey = Deno.env.get("GEMINI_API_KEY");
    const groqApiKey = Deno.env.get("GROQ_API_KEY");

    if (!supabaseUrl || !supabaseAnonKey || !supabaseServiceKey) {
      return new Response(
        JSON.stringify({
          error: "server_misconfiguration",
          message: "Supabase environment configuration missing on server.",
        }),
        { status: 500, headers: corsHeaders }
      );
    }

    // 3. User Authentication via Verified JWT
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

    // 4. Atomic Quota Enforcement & Invite Check (Weight = 4 units)
    const adminClient = createClient(supabaseUrl, supabaseServiceKey, {
      auth: { persistSession: false },
    });

    const { data: quota, error: quotaError } = await adminClient.rpc(
      "check_and_increment_usage",
      { p_user_id: user.id, p_weight: EXPLAIN_WEIGHT }
    );

    if (quotaError) {
      console.error("Quota check RPC error in visual-explain:", quotaError);
      return new Response(
        JSON.stringify({
          error: "quota_check_failed",
          message: "Database error verifying quota.",
          details: quotaError.message,
        }),
        { status: 500, headers: corsHeaders }
      );
    }

    if (!quota?.allowed) {
      if (quota?.error === "waitlist_pending") {
        return new Response(
          JSON.stringify({
            error: "waitlist_pending",
            message: "Your account is on the waitlist and awaiting invite approval.",
            date: quota.date,
            reset_at: quota.reset_at,
          }),
          { status: 403, headers: corsHeaders }
        );
      }

      return new Response(
        JSON.stringify({
          error: quota.error || "limit_reached",
          count: quota.count,
          limit: quota.limit,
          remaining: 0,
          plan: quota.plan || "Free",
          date: quota.date,
          reset_at: quota.reset_at,
        }),
        { status: 429, headers: corsHeaders }
      );
    }

    const usageDate = quota.date; // Partitioned date for refund on upstream error

    // 5. Parse Request Body (Supports JSON or Multipart)
    let base64Image = "";
    let question = "";
    const contentType = req.headers.get("content-type") || "";

    if (contentType.includes("multipart/form-data")) {
      try {
        const formData = await req.formData();
        question = (formData.get("prompt") as string) || (formData.get("question") as string) || "";
        const file = formData.get("file") || formData.get("image") || formData.get("screen");
        if (file instanceof Blob) {
          const buffer = await file.arrayBuffer();
          const bytes = new Uint8Array(buffer);
          let binary = "";
          const len = bytes.byteLength;
          for (let i = 0; i < len; i++) {
            binary += String.fromCharCode(bytes[i]);
          }
          base64Image = btoa(binary);
        }
      } catch (err: any) {
        await adminClient.rpc("decrement_usage", {
          p_user_id: user.id,
          p_usage_date: usageDate,
          p_weight: EXPLAIN_WEIGHT,
        });
        return new Response(
          JSON.stringify({
            error: "invalid_multipart_body",
            message: "Failed to parse multipart request: " + err?.message,
          }),
          { status: 400, headers: corsHeaders }
        );
      }
    } else {
      try {
        const json = await req.json();
        question = typeof json.question === "string" ? json.question.trim() : typeof json.prompt === "string" ? json.prompt.trim() : "";
        base64Image = typeof json.image === "string" ? json.image.trim() : "";
        if (base64Image.includes(",")) {
          base64Image = base64Image.split(",")[1];
        }
      } catch (err: any) {
        await adminClient.rpc("decrement_usage", {
          p_user_id: user.id,
          p_usage_date: usageDate,
          p_weight: EXPLAIN_WEIGHT,
        });
        return new Response(
          JSON.stringify({
            error: "invalid_json_body",
            message: "Failed to parse JSON body: " + err?.message,
          }),
          { status: 400, headers: corsHeaders }
        );
      }
    }

    if (!base64Image || base64Image.length < 50) {
      await adminClient.rpc("decrement_usage", {
        p_user_id: user.id,
        p_usage_date: usageDate,
        p_weight: EXPLAIN_WEIGHT,
      });
      return new Response(
        JSON.stringify({
          error: "missing_image",
          message: "Request must include a screenshot of the problem.",
        }),
        { status: 400, headers: corsHeaders }
      );
    }

    if (!question) {
      question = "Explain this math problem step by step.";
    }

    // 6. Invoke Vision Model with Enforced Structured JSON Schema
    let structuredResult: any = null;
    let upstreamError: string | null = null;

    // Strategy 1: Google Gemini Flash (gemini-3-flash-preview) with responseSchema
    if (geminiApiKey) {
      const candidateModels = ["gemini-3-flash-preview"];
      const geminiSchema = {
        type: "OBJECT",
        properties: {
          steps: {
            type: "ARRAY",
            items: {
              type: "OBJECT",
              properties: {
                narration: { type: "STRING" },
                draw: {
                  type: "ARRAY",
                  items: {
                    type: "OBJECT",
                    properties: {
                      type: { type: "STRING" },
                      text: { type: "STRING" },
                      x: { type: "NUMBER" },
                      y: { type: "NUMBER" },
                      x1: { type: "NUMBER" },
                      y1: { type: "NUMBER" },
                      x2: { type: "NUMBER" },
                      y2: { type: "NUMBER" },
                      w: { type: "NUMBER" },
                      h: { type: "NUMBER" },
                      cx: { type: "NUMBER" },
                      cy: { type: "NUMBER" },
                      r: { type: "NUMBER" },
                      size: { type: "NUMBER" },
                      color: { type: "STRING" },
                      label: { type: "STRING" },
                      dirX: { type: "NUMBER" },
                      dirY: { type: "NUMBER" },
                      start: { type: "NUMBER" },
                      end: { type: "NUMBER" },
                      orientation: { type: "STRING" },
                      min: { type: "NUMBER" },
                      max: { type: "NUMBER" },
                      length: { type: "NUMBER" },
                      originX: { type: "NUMBER" },
                      originY: { type: "NUMBER" },
                      lengthX: { type: "NUMBER" },
                      lengthY: { type: "NUMBER" },
                      strokeWidth: { type: "NUMBER" },
                      points: {
                        type: "ARRAY",
                        items: {
                          type: "ARRAY",
                          items: { type: "NUMBER" }
                        }
                      }
                    },
                    required: ["type"]
                  }
                }
              },
              required: ["narration", "draw"]
            }
          }
        },
        required: ["steps"]
      };

      const geminiPayload = {
        system_instruction: {
          parts: [{ text: SYSTEM_INSTRUCTION }]
        },
        contents: [
          {
            parts: [
              {
                inline_data: {
                  mime_type: "image/jpeg",
                  data: base64Image,
                },
              },
              {
                text: `User Question: ${question}\nAnalyze the problem shown in this screenshot. Provide a step-by-step whiteboard solution with spoken narrations and drawing primitives.`,
              },
            ],
          },
        ],
        generationConfig: {
          temperature: 0.1,
          maxOutputTokens: 2048,
          responseMimeType: "application/json",
          responseSchema: geminiSchema,
        },
      };

      for (const model of candidateModels) {
        const geminiUrl = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${encodeURIComponent(
          geminiApiKey
        )}`;

        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 15_000);

        try {
          const resp = await fetch(geminiUrl, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(geminiPayload),
            signal: controller.signal,
          });

          if (resp.ok) {
            const data = await resp.json();
            const rawText = data.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
            if (rawText) {
              try {
                const parsed = JSON.parse(rawText);
                if (parsed.steps && Array.isArray(parsed.steps) && parsed.steps.length > 0) {
                  structuredResult = parsed;
                  break;
                }
              } catch (parseErr) {
                console.warn(`Failed to parse Gemini output as JSON for ${model}:`, parseErr);
              }
            }
          } else {
            upstreamError = await resp.text();
            console.warn(`Gemini ${model} failed (${resp.status}): ${upstreamError}`);
          }
        } catch (err: any) {
          upstreamError = err?.message || "fetch_failed";
          console.warn(`Gemini ${model} threw error: ${upstreamError}`);
        } finally {
          clearTimeout(timeout);
        }
      }
    }

    // Strategy 2: Fallback to Groq Vision (llama-3.2-11b-vision-preview) if Gemini didn't succeed
    if (!structuredResult && groqApiKey) {
      try {
        const groqResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${groqApiKey}`,
          },
          body: JSON.stringify({
            model: "llama-3.2-11b-vision-preview",
            messages: [
              { role: "system", content: SYSTEM_INSTRUCTION },
              {
                role: "user",
                content: [
                  { type: "text", text: `User Question: ${question}\nReturn strict JSON with steps array.` },
                  { type: "image_url", image_url: { url: `data:image/jpeg;base64,${base64Image}` } }
                ]
              }
            ],
            response_format: { type: "json_object" },
            temperature: 0.1,
            max_tokens: 2048,
          }),
        });

        if (groqResp.ok) {
          const gData = await groqResp.json();
          const gText = gData.choices?.[0]?.message?.content?.trim() || "";
          if (gText) {
            const parsed = JSON.parse(gText);
            if (parsed.steps && Array.isArray(parsed.steps) && parsed.steps.length > 0) {
              structuredResult = parsed;
            }
          }
        } else {
          upstreamError = await groqResp.text();
        }
      } catch (gErr: any) {
        upstreamError = gErr?.message || "groq_failed";
      }
    }

    // 7. Validate and Sanitize Output
    if (!structuredResult || !Array.isArray(structuredResult.steps) || structuredResult.steps.length === 0) {
      // Upstream failed: automatically refund the 4 units
      await adminClient.rpc("decrement_usage", {
        p_user_id: user.id,
        p_usage_date: usageDate,
        p_weight: EXPLAIN_WEIGHT,
      });

      return new Response(
        JSON.stringify({
          error: "explain_upstream_failed",
          upstream_error: upstreamError,
          message: "Visual explain service could not generate solution steps. 4 units refunded.",
        }),
        { status: 502, headers: corsHeaders }
      );
    }

    // Sanitize each step and its drawing primitives
    const sanitizedSteps = structuredResult.steps.map((st: any) => ({
      narration: typeof st.narration === "string" ? st.narration.trim() : "Observe this step.",
      draw: Array.isArray(st.draw)
        ? st.draw
            .filter((d: any) => d && typeof d.type === "string")
            .map((d: any) => {
              const cleaned: Record<string, any> = { type: d.type.toLowerCase().trim() };
              for (const [k, v] of Object.entries(d)) {
                if (k === "type") continue;
                if (typeof v === "number" && !isNaN(v)) cleaned[k] = v;
                else if (typeof v === "string") cleaned[k] = v;
                else if (Array.isArray(v)) cleaned[k] = v;
                else if (typeof v === "boolean") cleaned[k] = v;
              }
              return cleaned;
            })
        : [],
    }));

    return new Response(
      JSON.stringify({
        steps: sanitizedSteps,
        count: quota.count,
        limit: quota.limit,
        remaining: quota.remaining,
        plan: quota.plan,
        reset_at: quota.reset_at,
      }),
      { status: 200, headers: corsHeaders }
    );
  } catch (err: any) {
    console.error("Unhandled visual-explain error:", err);
    return new Response(
      JSON.stringify({
        error: "internal_server_error",
        message: err?.message || "An unexpected error occurred.",
      }),
      { status: 500, headers: corsHeaders }
    );
  }
});
