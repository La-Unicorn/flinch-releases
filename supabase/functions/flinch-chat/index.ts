// ============================================================================
// FLINCH AI: SUPABASE EDGE FUNCTION (flinch-chat)
// Hardened Zero-Client-Key LLM Gateway with Atomic Quota & Auto-Refund
// ============================================================================

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.39.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Content-Type": "application/json; charset=utf-8",
};

// Hardened server-side constants (never client-controlled)
const MAX_BODY_BYTES = 16_384; // 16 KB request limit
const HARDCODED_TEMPERATURE = 0.3;
const HARDCODED_MAX_TOKENS = 140;
const ALLOWED_GROQ_MODELS = [
  "qwen/qwen3.8-27b",
  "llama-3.3-70b-versatile",
  "llama-3.1-8b-instant",
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

  // 2. Request Payload Size Guard
  const contentLength = parseInt(req.headers.get("content-length") || "0", 10);
  if (contentLength > MAX_BODY_BYTES) {
    return new Response(
      JSON.stringify({
        error: "payload_too_large",
        message: `Request body exceeds maximum allowed size of ${MAX_BODY_BYTES} bytes.`,
      }),
      { status: 413, headers: corsHeaders }
    );
  }

  try {
    const supabaseUrl = Deno.env.get("SUPABASE_URL");
    const supabaseAnonKey = Deno.env.get("SUPABASE_ANON_KEY");
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

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

    // 4. Atomic Quota Enforcement & Invite Check (Weight = 1)
    const adminClient = createClient(supabaseUrl, supabaseServiceKey, {
      auth: { persistSession: false },
    });

    const { data: quota, error: quotaError } = await adminClient.rpc(
      "check_and_increment_usage",
      { p_user_id: user.id, p_weight: 1 }
    );

    if (quotaError) {
      console.error("Quota check RPC error:", quotaError);
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

      // 429 limit reached: Return reset_at machine-readable timestamp
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

    const usageDate = quota.date; // Store exact partitioned date for refund if call fails

    // 5. Parse Query & Conversation Messages
    const body = await req.json().catch(() => ({}));
    const query = typeof body.query === "string" ? body.query.trim() : "";
    const messages = Array.isArray(body.messages) ? body.messages : [];

    let promptMessages: Array<{ role: string; content: string }> = [];

    // System prompt is controlled on server
    const systemPrompt =
      "You are Flinch, a desktop Dynamic Island assistant for PC. Keep answers extremely short, concise, direct, and factual (1 to 2 sentences max). Zero conversational filler.";
    promptMessages.push({ role: "system", content: systemPrompt });

    if (messages.length > 0) {
      // Keep last 4 turns to conserve tokens
      const recent = messages.slice(-4);
      for (const m of recent) {
        if (m.role && m.content) {
          promptMessages.push({
            role: m.role === "assistant" ? "assistant" : "user",
            content: String(m.content).slice(0, 1000),
          });
        }
      }
    } else if (query) {
      promptMessages.push({ role: "user", content: query.slice(0, 1500) });
    } else {
      // Refund if client query was completely empty
      await adminClient.rpc("decrement_usage", {
        p_user_id: user.id,
        p_usage_date: usageDate,
        p_weight: 1,
      });

      return new Response(
        JSON.stringify({ error: "empty_query", message: "Query text cannot be empty." }),
        { status: 400, headers: corsHeaders }
      );
    }

    // 6. Optional Server-Side Brave Search (Web Context)
    const braveKey = Deno.env.get("BRAVE_SEARCH_API_KEY");
    const userQuery = query || promptMessages[promptMessages.length - 1]?.content || "";
    const needsLiveSearch =
      /\b(weather|stock|crypto|price|news|score|today|game|latest|current|who won)\b/i.test(userQuery);

    if (needsLiveSearch && braveKey) {
      try {
        const braveUrl = `https://api.search.brave.com/res/v1/web/search?q=${encodeURIComponent(
          userQuery
        )}&count=3`;
        const bResp = await fetch(braveUrl, {
          headers: { "X-Subscription-Token": braveKey, Accept: "application/json" },
        });

        if (bResp.ok) {
          const bData = await bResp.json();
          const searchContext = (bData.web?.results || [])
            .map((r: { description?: string; title?: string }) => r.description || r.title)
            .join("\n");

          if (searchContext) {
            promptMessages.push({
              role: "system",
              content: `Live web search context:\n${searchContext.slice(0, 1000)}`,
            });
          }
        }
      } catch (searchErr) {
        console.warn("Brave search non-critical failure:", searchErr);
      }
    }

    // 7. Secure Inference with Groq (Fallback Gemini) & Upstream Auto-Refund
    const groqKey = Deno.env.get("GROQ_API_KEY");
    const geminiKey = Deno.env.get("GEMINI_API_KEY");

    let replyText = "";
    let upstreamError: string | null = null;
    let shouldRefund = false;

    if (groqKey) {
      for (const model of ALLOWED_GROQ_MODELS) {
        try {
          let groqResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              Authorization: `Bearer ${groqKey}`,
            },
            body: JSON.stringify({
              model,
              messages: promptMessages,
              temperature: HARDCODED_TEMPERATURE,
              max_tokens: HARDCODED_MAX_TOKENS,
            }),
          });

          // If Groq returns 429 with small Retry-After, do one short retry
          if (groqResp.status === 429) {
            const retryAfterSec = parseInt(groqResp.headers.get("retry-after") || "0", 10);
            if (retryAfterSec > 0 && retryAfterSec <= 2) {
              await new Promise((resolve) => setTimeout(resolve, retryAfterSec * 1000));
              groqResp = await fetch("https://api.groq.com/openai/v1/chat/completions", {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                  Authorization: `Bearer ${groqKey}`,
                },
                body: JSON.stringify({
                  model,
                  messages: promptMessages,
                  temperature: HARDCODED_TEMPERATURE,
                  max_tokens: HARDCODED_MAX_TOKENS,
                }),
              });
            }
          }

          if (groqResp.ok) {
            const groqData = await groqResp.json();
            replyText =
              groqData.choices?.[0]?.message?.content ||
              groqData.choices?.[0]?.message?.reasoning ||
              "";
            if (replyText) break;
          } else {
            const errText = await groqResp.text();
            upstreamError = `Groq ${groqResp.status}: ${errText}`;
            if (groqResp.status >= 500 || groqResp.status === 429) {
              shouldRefund = true;
            }
          }
        } catch (netErr) {
          upstreamError = `Groq network error: ${String(netErr)}`;
          shouldRefund = true;
        }
      }
    }

    // Fallback to Gemini if Groq failed
    if (!replyText && geminiKey) {
      try {
        const geminiUrl = `https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=${encodeURIComponent(
          geminiKey
        )}`;
        const contents = promptMessages
          .filter((m) => m.role !== "system")
          .map((m) => ({
            role: m.role === "assistant" ? "model" : "user",
            parts: [{ text: m.content }],
          }));

        const gemResp = await fetch(geminiUrl, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            contents,
            systemInstruction: { parts: [{ text: systemPrompt }] },
            generationConfig: {
              temperature: HARDCODED_TEMPERATURE,
              maxOutputTokens: HARDCODED_MAX_TOKENS,
            },
          }),
        });

        if (gemResp.ok) {
          const gemData = await gemResp.json();
          replyText = gemData.candidates?.[0]?.content?.parts?.[0]?.text || "";
          shouldRefund = false; // Successfully recovered with fallback!
        } else {
          const gemErr = await gemResp.text();
          upstreamError = `Gemini ${gemResp.status}: ${gemErr}`;
          if (gemResp.status >= 500 || gemResp.status === 429) {
            shouldRefund = true;
          }
        }
      } catch (gemNetErr) {
        upstreamError = `Gemini network error: ${String(gemNetErr)}`;
        shouldRefund = true;
      }
    }

    // 8. If Upstream Failed Completely: Refund the User
    if (!replyText) {
      if (shouldRefund) {
        console.warn("Refunding usage due to upstream failure:", upstreamError);
        await adminClient.rpc("decrement_usage", {
          p_user_id: user.id,
          p_usage_date: usageDate,
          p_weight: 1,
        });
      }

      return new Response(
        JSON.stringify({
          error: "upstream_unavailable",
          message: "AI inference service temporarily unavailable. Usage has been refunded.",
          details: upstreamError,
        }),
        { status: 503, headers: corsHeaders }
      );
    }

    // 9. Success Response
    const cleanedReply = replyText.replace(/^[\s"']+|[\s"']+$/g, "").trim();

    return new Response(
      JSON.stringify({
        reply: cleanedReply,
        remaining: quota.remaining,
        count: quota.count,
        limit: quota.limit,
        plan: quota.plan,
        reset_at: quota.reset_at,
      }),
      { status: 200, headers: corsHeaders }
    );
  } catch (err: unknown) {
    console.error("Edge function uncaught exception:", err);
    return new Response(
      JSON.stringify({
        error: "internal_server_error",
        message: err instanceof Error ? err.message : String(err),
      }),
      { status: 500, headers: corsHeaders }
    );
  }
});
