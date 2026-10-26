-- ============================================================================
-- FLINCH AI — 1-CLICK SUPABASE CLEANUP, SCHEMA & BACKEND VAULT SETUP
-- Paste into Supabase Dashboard -> SQL Editor -> Click "Run"
-- ============================================================================

BEGIN;

-- 1. CLEAN UP OLD PROTOTYPE TABLES, TRIGGERS & FUNCTIONS
DROP TRIGGER IF EXISTS on_auth_user_created ON auth.users;
DROP FUNCTION IF EXISTS public.handle_new_user() CASCADE;
DROP FUNCTION IF EXISTS public.handle_new_flinch_user() CASCADE;
DROP FUNCTION IF EXISTS public.get_flinch_runtime_config() CASCADE;
DROP FUNCTION IF EXISTS public.update_flinch_voice(text) CASCADE;

DROP TABLE IF EXISTS public.flinch_logs CASCADE;
DROP TABLE IF EXISTS public.flinch_usage CASCADE;
DROP TABLE IF EXISTS public.flinch_sessions CASCADE;
DROP TABLE IF EXISTS public.flinch_devices CASCADE;
DROP TABLE IF EXISTS public.user_profiles CASCADE;
DROP TABLE IF EXISTS public.profiles CASCADE;
DROP SCHEMA IF EXISTS flinch_private CASCADE;

-- Optional: Clean up old unverified prototype test accounts (keeps real accounts intact, or uncomment below to wipe all users)
DELETE FROM auth.users WHERE email IN ('test@gmail.com', 'demo@flinch.ai', 'test@example.com');

-- 2. CREATE CLEAN USER PROFILES TABLE WITH ROW-LEVEL SECURITY (RLS)
CREATE TABLE public.profiles (
    id UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    email TEXT NOT NULL,
    preferred_voice TEXT NOT NULL DEFAULT 'troy',
    tier TEXT NOT NULL DEFAULT 'free',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can view their own profile"
    ON public.profiles FOR SELECT
    TO authenticated
    USING (auth.uid() = id);

CREATE POLICY "Users can update their own profile"
    ON public.profiles FOR UPDATE
    TO authenticated
    USING (auth.uid() = id)
    WITH CHECK (auth.uid() = id);

CREATE POLICY "Users can insert their own profile"
    ON public.profiles FOR INSERT
    TO authenticated
    WITH CHECK (auth.uid() = id);

-- Automatically create a profile row when a user signs up
CREATE OR REPLACE FUNCTION public.handle_new_flinch_user()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    INSERT INTO public.profiles (id, email, preferred_voice, tier)
    VALUES (
        NEW.id,
        COALESCE(NEW.email, ''),
        'troy',
        'free'
    )
    ON CONFLICT (id) DO UPDATE
    SET email = EXCLUDED.email,
        updated_at = now();
    RETURN NEW;
END;
$$;

CREATE TRIGGER on_auth_user_created
    AFTER INSERT ON auth.users
    FOR EACH ROW EXECUTE FUNCTION public.handle_new_flinch_user();

-- Backfill profiles for any existing users in auth.users
INSERT INTO public.profiles (id, email)
SELECT id, COALESCE(email, '')
FROM auth.users
ON CONFLICT (id) DO NOTHING;

-- 3. LOCKED-DOWN BACKEND VAULT FOR AI KEYS (Zero Public/Anon Access)
CREATE SCHEMA flinch_private;
REVOKE ALL ON SCHEMA flinch_private FROM PUBLIC, anon, authenticated;

CREATE TABLE flinch_private.secrets (
    key_name TEXT PRIMARY KEY,
    key_value TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

REVOKE ALL ON ALL TABLES IN SCHEMA flinch_private FROM PUBLIC, anon, authenticated;

-- Seed backend keys (populated from your server configuration)
INSERT INTO flinch_private.secrets (key_name, key_value) VALUES
    ('GROQ_API_KEY',   '__GROQ_API_KEY__'),
    ('GEMINI_API_KEY', '__GEMINI_API_KEY__'),
    ('EXA_API_KEY',    '__EXA_API_KEY__')
ON CONFLICT (key_name) DO UPDATE
SET key_value = EXCLUDED.key_value,
    updated_at = now();

-- 4. AUTHENTICATED-ONLY RPC TO FETCH RUNTIME CONFIG & PREFERRED VOICE
CREATE OR REPLACE FUNCTION public.get_flinch_runtime_config()
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, flinch_private
AS $$
DECLARE
    v_uid UUID;
    v_voice TEXT;
    v_tier TEXT;
    v_groq TEXT;
    v_gemini TEXT;
    v_exa TEXT;
BEGIN
    v_uid := auth.uid();
    IF v_uid IS NULL THEN
        RAISE EXCEPTION 'Unauthorized: valid Flinch account session required.';
    END IF;

    SELECT preferred_voice, tier
      INTO v_voice, v_tier
      FROM public.profiles
     WHERE id = v_uid;

    IF v_voice IS NULL THEN
        v_voice := 'troy';
        v_tier := 'free';
    END IF;

    SELECT key_value INTO v_groq   FROM flinch_private.secrets WHERE key_name = 'GROQ_API_KEY';
    SELECT key_value INTO v_gemini FROM flinch_private.secrets WHERE key_name = 'GEMINI_API_KEY';
    SELECT key_value INTO v_exa    FROM flinch_private.secrets WHERE key_name = 'EXA_API_KEY';

    RETURN jsonb_build_object(
        'user_id',         v_uid,
        'preferred_voice', v_voice,
        'tier',            v_tier,
        'groq_api_key',    COALESCE(v_groq, ''),
        'gemini_api_key',  COALESCE(v_gemini, ''),
        'exa_api_key',     COALESCE(v_exa, '')
    );
END;
$$;

REVOKE ALL ON FUNCTION public.get_flinch_runtime_config() FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.get_flinch_runtime_config() TO authenticated;

-- 5. AUTHENTICATED RPC TO SAVE USER'S PREFERRED VOICE
CREATE OR REPLACE FUNCTION public.update_flinch_voice(new_voice TEXT)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    IF auth.uid() IS NULL THEN
        RAISE EXCEPTION 'Unauthorized';
    END IF;

    UPDATE public.profiles
       SET preferred_voice = COALESCE(NULLIF(TRIM(new_voice), ''), 'troy'),
           updated_at = now()
     WHERE id = auth.uid();
END;
$$;

REVOKE ALL ON FUNCTION public.update_flinch_voice(TEXT) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.update_flinch_voice(TEXT) TO authenticated;

COMMIT;
