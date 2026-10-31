-- The two places of the transactional outbox (TF-085, ADR-0060), as outbox-place.sh measures them: one kernel table, or
-- one table in each of ten module schemas, each with the dispatcher's partial index on what it has not handed on yet.

create schema kernel_events;

create table kernel_events.outbox (
    position bigint generated always as identity primary key,
    id uuid not null unique,
    type text not null,
    version int not null,
    record_type text,
    record_id uuid,
    occurred_at timestamptz not null,
    data jsonb not null,
    fanned_at timestamptz);
create index outbox_pending on kernel_events.outbox (position) where fanned_at is null;

create table business (id uuid primary key, name text not null, version int not null);
create table audit (id bigint generated always as identity primary key, action text not null, record_id uuid, at timestamptz not null);

do $$
begin
    for m in 1..10 loop
        execute format('create schema mod_%s', m);
        execute format('create table mod_%s.outbox (like kernel_events.outbox including all)', m);
    end loop;
end $$;

create function truncate_modules() returns void language plpgsql as $$
begin
    for m in 1..10 loop
        execute format('truncate mod_%s.outbox', m);
    end loop;
end $$;

-- A million events handed on and a hundred waiting in the kernel table; the same split over the ten module tables.
create function fill_backlogs() returns void language plpgsql as $$
begin
    truncate kernel_events.outbox;
    perform truncate_modules();
    insert into kernel_events.outbox (id, type, version, record_type, record_id, occurred_at, data, fanned_at)
        select gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x"}', now()
        from generate_series(1, 1000000);
    insert into kernel_events.outbox (id, type, version, record_type, record_id, occurred_at, data)
        select gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x"}'
        from generate_series(1, 100);
    for m in 1..10 loop
        execute format($f$insert into mod_%s.outbox (id, type, version, record_type, record_id, occurred_at, data, fanned_at)
            select gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x"}', now()
            from generate_series(1, 100000)$f$, m);
        execute format($f$insert into mod_%s.outbox (id, type, version, record_type, record_id, occurred_at, data)
            select gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x"}'
            from generate_series(1, 10)$f$, m);
    end loop;
    analyze;
end $$;

-- The median of a statement's runs, in milliseconds.
create function median_ms(statement text, runs int) returns numeric language plpgsql as $$
declare
    started timestamptz;
    took numeric[] := '{}';
begin
    for i in 1..runs loop
        started := clock_timestamp();
        execute statement;
        took := took || extract(epoch from clock_timestamp() - started) * 1000;
    end loop;
    return (select round(percentile_cont(0.5) within group (order by t)::numeric, 3) from unnest(took) t);
end $$;

create function poll_timings() returns table (poll text, kernel_table_ms numeric, module_tables_ms numeric) language plpgsql as $$
declare
    kernel_poll text := 'select position from kernel_events.outbox where fanned_at is null order by position limit 100';
    module_poll text := (select string_agg(format('(select position from mod_%s.outbox where fanned_at is null order by position limit 100)', m), ' union all ') from generate_series(1, 10) m);
begin
    poll := 'With 100 waiting beside 1,000,000 handed on';
    kernel_table_ms := median_ms(kernel_poll, 200);
    module_tables_ms := median_ms(module_poll, 200);
    return next;
    update kernel_events.outbox set fanned_at = now() where fanned_at is null;
    for m in 1..10 loop
        execute format('update mod_%s.outbox set fanned_at = now() where fanned_at is null', m);
    end loop;
    poll := 'Idle: nothing waiting';
    kernel_table_ms := median_ms(kernel_poll, 200);
    module_tables_ms := median_ms(module_poll, 200);
    return next;
end $$;
