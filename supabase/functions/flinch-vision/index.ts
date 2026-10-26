// ============================================================================
// FLINCH AI: SUPABASE EDGE FUNCTION (flinch-vision)
// Server-Side Multimodal Vision & Coordinate Grounding (Gemini 2.0 Flash)
// Cost: 2 Quota Units | Atomic Rate-Limiting & Auto-Refund
// ============================================================================

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.39.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Content-Type": "application/json; charset=utf-8",
};

const MAX_PAYLOAD_BYTES = 10 * 1024 * 1024; // 10 MB maximum screenshot size
const VISION_WEIGHT = 2; // Vision consumes 2 units per request
const GEMINI_MODELS = [
  "gemini-3.8-flash",
  "gemini-2.5-flash",
  "gemini-1.5-flash",
] as const;

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

    if (!supabaseUrl || !supabaseAnonKey || !supabaseServiceKey || !geminiApiKey) {
      return new Response(
        JSON.stringify({
          error: "server_misconfiguration",
          message: "Supabase or Gemini environment configuration missing on server.",
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

    // 4. Atomic Quota Enforcement & Invite Check (Weight = 2 units)
    const adminClient = createClient(supabaseUrl, supabaseServiceKey, {
      auth: { persistSession: false },
    });

    const { data: quota, error: quotaError } = await adminClient.rpc(
      "check_and_increment_usage",
      { p_user_id: user.id, p_weight: VISION_WEIGHT }
    );

    if (quotaError) {
      console.error("Quota check RPC error in flinch-vision:", quotaError);
      return new Response(
        JSON.stringify({
          error: "quota_check_failed",
          message: "Database error verifying quota.",
          details: quotaError.message,
        }),
        { status: 500, headers: corsHeaders }
      );
    }

    // Handle waitlist pending or quota rejection
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
    let prompt = "";
    const contentType = req.headers.get("content-type") || "";

    if (contentType.includes("multipart/form-data")) {
      try {
        const formData = await req.formData();
        prompt = (formData.get("prompt") as string) || "";
        const file = formData.get("file") || formData.get("image");
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
          p_weight: VISION_WEIGHT,
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
      // Default JSON payload
      try {
        const json = await req.json();
        prompt = typeof json.prompt === "string" ? json.prompt.trim() : "";
        base64Image = typeof json.image === "string" ? json.image.trim() : "";
        if (base64Image.includes(",")) {
          // Remove data:image/jpeg;base64, prefix if present
          base64Image = base64Image.split(",")[1];
        }
      } catch (err: any) {
        await adminClient.rpc("decrement_usage", {
          p_user_id: user.id,
          p_usage_date: usageDate,
          p_weight: VISION_WEIGHT,
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
        p_weight: VISION_WEIGHT,
      });
      return new Response(
        JSON.stringify({
          error: "missing_image",
          message: "Request must include a valid JPEG image (base64 or binary file).",
        }),
        { status: 400, headers: corsHeaders }
      );
    }

    if (!prompt) {
      prompt = "Describe this screenshot and identify clickable UI elements with their bounding coordinates.";
    }

    // 6. Invoke Google Gemini Multimodal Vision API (with dynamic model discovery)
    let resultText = "";
    let lastUpstreamStatus = 502;
    let lastUpstreamError = "All Gemini models failed";

    // Query Gemini ModelService dynamically to get valid active model names
    let candidateModels: string[] = [];
    try {
      const listResp = await fetch(
        `https://generativelanguage.googleapis.com/v1beta/models?key=${encodeURIComponent(geminiApiKey)}`
      );
      if (listResp.ok) {
        const listJson = await listResp.json();
        const models = listJson.models || [];
        candidateModels = models
          .filter(
            (m: any) =>
              Array.isArray(m.supportedGenerationMethods) &&
              m.supportedGenerationMethods.includes("generateContent")
          )
          .map((m: any) => m.name.replace(/^models\//, ""))
          .filter((name: string) => name.includes("flash") || name.includes("gemini"));
        console.log("Discovered Gemini models:", candidateModels);
      } else {
        lastUpstreamError = await listResp.text();
        console.warn("Gemini ListModels error:", listResp.status, lastUpstreamError);
      }
    } catch (listErr: any) {
      console.warn("Failed to list Gemini models:", listErr?.message);
    }

    if (candidateModels.length === 0) {
      candidateModels = ["gemini-1.5-flash-latest", "gemini-1.5-flash", "gemini-2.0-flash-exp"];
    }

    const geminiPayload = {
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
              text: prompt,
            },
          ],
        },
      ],
      generationConfig: {
        temperature: 0.2,
        maxOutputTokens: 1024,
      },
    };

    for (const model of candidateModels) {
      const geminiUrl = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${encodeURIComponent(
        geminiApiKey
      )}`;

      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 20_000);

      try {
        const gemResp = await fetch(geminiUrl, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(geminiPayload),
          signal: controller.signal,
        });

        if (gemResp.ok) {
          const gemJson = await gemResp.json();
          resultText = gemJson.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
          if (resultText) break;
        } else {
          lastUpstreamStatus = gemResp.status;
          lastUpstreamError = await gemResp.text();
          console.warn(`Gemini model ${model} failed (${gemResp.status}): ${lastUpstreamError}`);
        }
      } catch (fetchErr: any) {
        lastUpstreamError = fetchErr?.message || "fetch_failed";
        console.warn(`Gemini model ${model} threw error: ${lastUpstreamError}`);
      } finally {
        clearTimeout(timeout);
      }
    }

    if (!resultText) {
      // Refund the 2 units on failure
      await adminClient.rpc("decrement_usage", {
        p_user_id: user.id,
        p_usage_date: usageDate,
        p_weight: VISION_WEIGHT,
      });

      return new Response(
        JSON.stringify({
          error: "vision_upstream_failed",
          upstream_status: lastUpstreamStatus,
          upstream_error: lastUpstreamError,
          discovered_models: candidateModels,
          message: "Gemini vision service encountered an error. 2 quota units refunded.",
        }),
        { status: 502, headers: corsHeaders }
      );
    }

    return new Response(
      JSON.stringify({
        text: resultText,
        count: quota.count,
        limit: quota.limit,
        remaining: quota.remaining,
        plan: quota.plan,
        reset_at: quota.reset_at,
      }),
      { status: 200, headers: corsHeaders }
    );
  } catch (err: any) {
    console.error("Unhandled flinch-vision error:", err);
    return new Response(
      JSON.stringify({
        error: "internal_server_error",
        message: err?.message || "An unexpected error occurred.",
      }),
      { status: 500, headers: corsHeaders }
    );
  }
});
