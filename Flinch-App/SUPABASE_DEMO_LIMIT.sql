-- ============================================================================
-- FLINCH DEMO — 5 VOICE MESSAGES PER ACCOUNT (server-side enforced)
-- Run AFTER SUPABASE_CLEANUP_AND_SETUP.sql. Safe to re-run. Contains no secrets.
-- ============================================================================
BEGIN;

ALTER TABLE public.profiles
    ADD COLUMN IF NOT EXISTS voice_messages_used  INT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS voice_message_limit  INT NOT NULL DEFAULT 5;

-- Users must NOT be able to edit their own counter/limit/tier directly.
-- Profile changes only happen through the SECURITY DEFINER functions below.
DROP POLICY IF EXISTS "Users can update their own profile" ON public.profiles;
DROP POLICY IF EXISTS "Users can insert their own profile" ON public.profiles;
REVOKE INSERT, UPDATE, DELETE ON public.profiles FROM anon, authenticated;

-- Atomically spend one voice message. Single UPDATE => no race conditions.
CREATE OR REPLACE FUNCTION public.consume_voice_message()
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_uid   UUID := auth.uid();
    v_used  INT;
    v_limit INT;
BEGIN
    IF v_uid IS NULL THEN
        RAISE EXCEPTION 'Unauthorized';
    END IF;

    UPDATE public.profiles
       SET voice_messages_used = voice_messages_used + 1,
           updated_at = now()
     WHERE id = v_uid
       AND voice_messages_used < voice_message_limit
    RETURNING voice_messages_used, voice_message_limit INTO v_used, v_limit;

    IF FOUND THEN
        RETURN jsonb_build_object('allowed', true, 'used', v_used, 'limit', v_limit,
                                  'remaining', v_limit - v_used);
    END IF;

    SELECT voice_messages_used, voice_message_limit INTO v_used, v_limit
      FROM public.profiles WHERE id = v_uid;

    RETURN jsonb_build_object('allowed', false, 'used', COALESCE(v_used, 0),
                              'limit', COALESCE(v_limit, 5), 'remaining', 0);
END;
$$;

REVOKE ALL ON FUNCTION public.consume_voice_message() FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.consume_voice_message() TO authenticated;

-- Runtime config: AI keys are only handed out while the account still has messages left.
CREATE OR REPLACE FUNCTION public.get_flinch_runtime_config()
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, flinch_private
AS $$
DECLARE
    v_uid    UUID := auth.uid();
    v_voice  TEXT;
    v_tier   TEXT;
    v_used   INT;
    v_limit  INT;
    v_remain INT;
    v_groq   TEXT := '';
    v_gemini TEXT := '';
    v_exa    TEXT := '';
BEGIN
    IF v_uid IS NULL THEN
        RAISE EXCEPTION 'Unauthorized: valid Flinch account session required.';
    END IF;

    SELECT preferred_voice, tier, voice_messages_used, voice_message_limit
      INTO v_voice, v_tier, v_used, v_limit
      FROM public.profiles WHERE id = v_uid;

    v_voice  := COALESCE(v_voice, 'troy');
    v_tier   := COALESCE(v_tier, 'free');
    v_used   := COALESCE(v_used, 0);
    v_limit  := COALESCE(v_limit, 5);
    v_remain := GREATEST(v_limit - v_used, 0);

    IF v_remain > 0 THEN
        SELECT key_value INTO v_groq   FROM flinch_private.secrets WHERE key_name = 'GROQ_API_KEY';
        SELECT key_value INTO v_gemini FROM flinch_private.secrets WHERE key_name = 'GEMINI_API_KEY';
        SELECT key_value INTO v_exa    FROM flinch_private.secrets WHERE key_name = 'EXA_API_KEY';
    END IF;

    RETURN jsonb_build_object(
        'user_id',         v_uid,
        'preferred_voice', v_voice,
        'tier',            v_tier,
        'voice_used',      v_used,
        'voice_limit',     v_limit,
        'voice_remaining', v_remain,
        'groq_api_key',    COALESCE(v_groq, ''),
        'gemini_api_key',  COALESCE(v_gemini, ''),
        'exa_api_key',     COALESCE(v_exa, '')
    );
END;
$$;

REVOKE ALL ON FUNCTION public.get_flinch_runtime_config() FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.get_flinch_runtime_config() TO authenticated;

COMMIT;
