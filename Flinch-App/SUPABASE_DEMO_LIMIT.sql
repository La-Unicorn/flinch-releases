-- ============================================================================
-- Flinch Beta Quota: 15 Agent Tasks Per Month + Zero-Key Runtime Config
-- Run this in your Supabase SQL Editor (Project -> SQL Editor -> New Query)
-- ============================================================================

-- 1. Ensure quota columns exist on public.profiles (Default = 15 tasks/month)
alter table public.profiles
  add column if not exists voice_messages_used integer not null default 0,
  add column if not exists voice_message_limit integer not null default 15;

alter table public.profiles
  alter column voice_message_limit set default 15;

-- Upgrade any existing free/beta demo accounts from old 5 limit to 15/month
update public.profiles
set voice_message_limit = 15
where voice_message_limit < 15;

-- 2. Trigger function: block any client-side attempt to change quota columns via REST PATCH
create or replace function public.protect_profile_quota_columns()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  if current_setting('request.jwt.claim.role', true) = 'authenticated' then
    new.voice_messages_used := old.voice_messages_used;
    new.voice_message_limit := old.voice_message_limit;
    new.tier                := old.tier;
  end if;
  new.updated_at := now();
  return new;
end;
$$;

drop trigger if exists trg_protect_profile_quota on public.profiles;
create trigger trg_protect_profile_quota
  before update on public.profiles
  for each row execute function public.protect_profile_quota_columns();

-- 3. Atomic RPC called before each agent task to spend 1 monthly task unit (with monthly auto-reset)
create or replace function public.consume_voice_message()
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  uid uuid := auth.uid();
  used_count integer;
  limit_count integer;
begin
  if uid is null then
    raise exception 'not_authenticated';
  end if;

  insert into public.profiles (id, email, voice_messages_used, voice_message_limit)
  select uid, u.email, 0, 15
  from auth.users u where u.id = uid
  on conflict (id) do nothing;

  -- Auto-reset monthly usage on new calendar month & ensure minimum 15 limit
  update public.profiles
  set voice_messages_used = case
        when date_trunc('month', updated_at) < date_trunc('month', now()) then 0
        else voice_messages_used
      end,
      voice_message_limit = greatest(voice_message_limit, 15)
  where id = uid;

  update public.profiles
  set voice_messages_used = voice_messages_used + 1,
      updated_at = now()
  where id = uid
    and voice_messages_used < voice_message_limit
  returning voice_messages_used, voice_message_limit
  into used_count, limit_count;

  if not found then
    select voice_messages_used, voice_message_limit
    into used_count, limit_count
    from public.profiles where id = uid;

    return jsonb_build_object(
      'allowed', false,
      'used', coalesce(used_count, 15),
      'limit', coalesce(limit_count, 15),
      'remaining', 0
    );
  end if;

  return jsonb_build_object(
    'allowed', true,
    'used', used_count,
    'limit', limit_count,
    'remaining', greatest(0, limit_count - used_count)
  );
end;
$$;

revoke all on function public.consume_voice_message() from public, anon;
grant execute on function public.consume_voice_message() to authenticated;

-- 4. Runtime config RPC: NEVER returns API keys to the client!
create or replace function public.get_flinch_runtime_config()
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  uid uuid := auth.uid();
  prof public.profiles%rowtype;
begin
  if uid is null then
    raise exception 'not_authenticated';
  end if;

  insert into public.profiles (id, email, voice_messages_used, voice_message_limit)
  select uid, u.email, 0, 15
  from auth.users u where u.id = uid
  on conflict (id) do nothing;

  update public.profiles
  set voice_messages_used = case
        when date_trunc('month', updated_at) < date_trunc('month', now()) then 0
        else voice_messages_used
      end,
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

revoke all on function public.get_flinch_runtime_config() from public, anon;
grant execute on function public.get_flinch_runtime_config() to authenticated;

-- 5. Service-role helper RPC for Edge Function (flinch-voice) to atomically consume 1 monthly task
create or replace function public.consume_voice_message_for_user(p_user_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  used_count integer;
  limit_count integer;
begin
  if p_user_id is null then
    raise exception 'missing_user_id';
  end if;

  insert into public.profiles (id, email, voice_messages_used, voice_message_limit)
  select p_user_id, u.email, 0, 15
  from auth.users u where u.id = p_user_id
  on conflict (id) do nothing;

  -- Reset counter if last activity was in a previous calendar month & ensure limit >= 15
  update public.profiles
  set voice_messages_used = case
        when date_trunc('month', updated_at) < date_trunc('month', now()) then 0
        else voice_messages_used
      end,
      voice_message_limit = greatest(voice_message_limit, 15)
  where id = p_user_id;

  update public.profiles
  set voice_messages_used = voice_messages_used + 1,
      updated_at = now()
  where id = p_user_id
    and voice_messages_used < voice_message_limit
  returning voice_messages_used, voice_message_limit
  into used_count, limit_count;

  if not found then
    select voice_messages_used, voice_message_limit
    into used_count, limit_count
    from public.profiles where id = p_user_id;

    return jsonb_build_object(
      'allowed', false,
      'used', coalesce(used_count, 15),
      'limit', coalesce(limit_count, 15),
      'remaining', 0
    );
  end if;

  return jsonb_build_object(
    'allowed', true,
    'used', used_count,
    'limit', limit_count,
    'remaining', greatest(0, limit_count - used_count)
  );
end;
$$;

-- 6. Service-role helper RPC for Edge Function to refund 1 task if upstream fails
create or replace function public.refund_voice_message_for_user(p_user_id uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
  update public.profiles
  set voice_messages_used = greatest(0, voice_messages_used - 1),
      updated_at = now()
  where id = p_user_id;
end;
$$;

revoke all on function public.consume_voice_message_for_user(uuid) from public, anon, authenticated;
grant execute on function public.consume_voice_message_for_user(uuid) to service_role;

revoke all on function public.refund_voice_message_for_user(uuid) from public, anon, authenticated;
grant execute on function public.refund_voice_message_for_user(uuid) to service_role;
