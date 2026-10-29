-- Visibility tags on records, by their codes: one kernel table joined in the query, against a tag column on the module's own table
-- (TF-241, V1). Run by visibility-tags-storage.sh on a throwaway PostgreSQL; the result is in ADR-0057.
--
-- The sample is a module's table of 120,000 records in 20 units, with the kernel's record shares, and the tags of a second
-- module's 120,000 records beside them in the kernel table, as every module's tags share it. Twelve tags; a record carries
-- none (30 %), one (50 %), two (15 %) or three (5 %). The queries are the ones the record policy builds: the scope rule
-- with the sharing mode, then the tag filter, for a list page with its total and for a read by identifier.

\set ON_ERROR_STOP on
select setseed(0.42);

create schema kernel_sample;
create schema kernel_data;

create table kernel_sample.measured_records (
    id uuid primary key,
    owning_organisation_id uuid not null,
    sharing_mode integer not null default 0,
    name text not null,
    reference text not null,
    amount numeric(18, 2) not null,
    status integer not null,
    created_at timestamptz not null,
    created_by uuid not null,
    updated_at timestamptz not null,
    updated_by uuid not null,
    version integer not null,
    visibility_tags text[] not null default '{}'
);

create table kernel_data.record_shares (
    id uuid primary key,
    record_type varchar(100) not null,
    record_id uuid not null,
    unit_id uuid not null,
    created_at timestamptz not null,
    created_by uuid not null,
    updated_at timestamptz not null,
    updated_by uuid not null,
    version integer not null
);

create table kernel_data.record_tags (
    id uuid primary key,
    record_type varchar(100) not null,
    record_id uuid not null,
    tag_code varchar(32) not null,
    created_at timestamptz not null,
    created_by uuid not null,
    updated_at timestamptz not null,
    updated_by uuid not null,
    version integer not null
);

create temporary table units as select uuidv7() as id, n from generate_series(1, 20) n;
create temporary table tags as select 'TAG' || lpad(n::text, 2, '0') as code, n from generate_series(1, 12) n;

-- How many tags a record carries, and which: distinct tags, the first ones the common ones (a region before a project).
create or replace function pg_temp.some_tags(r double precision) returns text[] language plpgsql volatile as $$
declare
    wanted integer := case when r < 0.30 then 0 when r < 0.80 then 1 when r < 0.95 then 2 else 3 end;
    picked integer[] := '{}';
    next integer;
begin
    while cardinality(picked) < wanted loop
        next := 1 + floor(power(random(), 1.6) * 12)::integer;
        if not next = any(picked) then
            picked := picked || next;
        end if;
    end loop;
    return array(select code from tags where n = any(picked) order by n);
end
$$;

insert into kernel_sample.measured_records (id, owning_organisation_id, sharing_mode, name, reference, amount, status, created_at, created_by, updated_at, updated_by, version, visibility_tags)
select uuidv7(),
       (select id from units where n = 1 + (g % 20)),
       case when g % 50 = 0 then 1 when g % 97 = 0 then 2 else 0 end,
       'Record ' || lpad(((g * 7919) % 120000)::text, 6, '0'),
       'REF-' || g,
       round((random() * 100000)::numeric, 2),
       g % 4,
       now() - (g || ' seconds')::interval, uuidv7(), now(), uuidv7(), 1,
       pg_temp.some_tags(random())
from generate_series(1, 120000) g;

-- The shared records are listed for two other units each.
insert into kernel_data.record_shares
select uuidv7(), 'measured_record', r.id, u.id, now(), uuidv7(), now(), uuidv7(), 1
from kernel_sample.measured_records r
join units u on u.n in (1 + (abs(hashtext(r.id::text)) % 20), 1 + (abs(hashtext(r.reference)) % 20))
where r.sharing_mode = 1
on conflict do nothing;

-- The kernel table: this module's records' tags, from the same draw as the column, and a second module's beside them.
insert into kernel_data.record_tags
select uuidv7(), 'measured_record', r.id, t, now(), uuidv7(), now(), uuidv7(), 1
from kernel_sample.measured_records r, unnest(r.visibility_tags) t;

insert into kernel_data.record_tags
select uuidv7(), 'other_record', uuidv7(), t, now(), uuidv7(), now(), uuidv7(), 1
from generate_series(1, 120000) g, unnest(pg_temp.some_tags(random())) t;

-- The indexes the model would declare: the module's (its unit, its sort), the shares' (as ADR-0038), and either the
-- kernel table's (by record, and by tag for "who carries it") or the column's.
create index on kernel_sample.measured_records (owning_organisation_id);
create index on kernel_sample.measured_records (name);
create unique index on kernel_data.record_shares (record_type, record_id, unit_id);
create index on kernel_data.record_shares (unit_id);
create unique index on kernel_data.record_tags (record_type, record_id, tag_code);
create index on kernel_data.record_tags (tag_code);
create index measured_records_visibility_tags on kernel_sample.measured_records using gin (visibility_tags);
vacuum analyze;

-- Runs a query a number of times after a warm-up, and answers its median and 95th percentile in milliseconds.
create or replace function pg_temp.measure(q text, runs integer) returns table (median_ms numeric, p95_ms numeric) language plpgsql as $$
declare
    started timestamptz;
    took double precision[] := '{}';
begin
    for i in 1..5 loop
        execute q;
    end loop;
    for i in 1..runs loop
        started := clock_timestamp();
        execute q;
        took := took || extract(epoch from clock_timestamp() - started) * 1000;
    end loop;
    return query
    select round(percentile_cont(0.5) within group (order by x)::numeric, 2), round(percentile_cont(0.95) within group (order by x)::numeric, 2)
    from unnest(took) x;
end
$$;

create temporary table readers (name text, units uuid[], held text[]);
insert into readers values
    ('every unit, one tag', array(select id from units), array(select code from tags where n = 3)),
    ('five units, two tags', array(select id from units where n <= 5), array(select code from tags where n in (2, 7)));

-- One record each reader may see by its unit and its tags, for the read by identifier.
create temporary table targets as
select rd.name, (
    select r.id from kernel_sample.measured_records r
    where r.owning_organisation_id = any(rd.units) and r.visibility_tags && rd.held
    order by r.id offset 1000 limit 1) as id
from readers rd;

create temporary table results (reader text, query text, storage text, median_ms numeric, p95_ms numeric, rows_seen bigint);

do $$
declare
    rd record;
    scope text;
    joined text;
    columned text;
    columns text := 'r.id, r.owning_organisation_id, r.sharing_mode, r.name, r.reference, r.amount, r.status, r.created_at, r.created_by, r.updated_at, r.updated_by, r.version';
    target uuid;
    seen bigint;
    q text;
    m record;
begin
    for rd in select * from readers loop
        scope := format(
            '(r.owning_organisation_id = any(%L::uuid[]) or r.sharing_mode = 2 or (r.sharing_mode = 1 and exists (select 1 from kernel_data.record_shares s where s.record_type = ''measured_record'' and s.record_id = r.id and s.unit_id = any(%L::uuid[]))))',
            rd.units, rd.units);
        joined := format(
            '(not exists (select 1 from kernel_data.record_tags t where t.record_type = ''measured_record'' and t.record_id = r.id) or exists (select 1 from kernel_data.record_tags t where t.record_type = ''measured_record'' and t.record_id = r.id and t.tag_code = any(%L::text[])))',
            rd.held);
        columned := format('(cardinality(r.visibility_tags) = 0 or r.visibility_tags && %L::text[])', rd.held);
        select id into target from targets where name = rd.name;

        foreach q in array array['none', 'kernel table', 'column'] loop
            declare
                filter text := case q when 'none' then 'true' when 'kernel table' then joined else columned end;
            begin
                execute format('select count(*) from kernel_sample.measured_records r where %s and %s', scope, filter) into seen;
                select * into m from pg_temp.measure(format('select %s from kernel_sample.measured_records r where %s and %s order by r.name limit 50', columns, scope, filter), 40);
                insert into results values (rd.name, 'list page of 50', q, m.median_ms, m.p95_ms, seen);
                select * into m from pg_temp.measure(format('select count(*) from kernel_sample.measured_records r where %s and %s', scope, filter), 40);
                insert into results values (rd.name, 'total of the list', q, m.median_ms, m.p95_ms, seen);
                select * into m from pg_temp.measure(format('select %s from kernel_sample.measured_records r where r.id = %L and %s and %s', columns, target, scope, filter), 200);
                insert into results values (rd.name, 'read by identifier', q, m.median_ms, m.p95_ms, 1);
            end;
        end loop;
    end loop;
end
$$;

\echo
select (select count(*) from kernel_sample.measured_records) as records,
       (select count(*) from kernel_data.record_tags) as kernel_table_rows,
       (select count(*) from kernel_data.record_shares) as shares,
       (select round(100.0 * count(*) filter (where cardinality(visibility_tags) = 0) / count(*), 1) from kernel_sample.measured_records) as untagged_percent,
       version() as server;

select reader, query, storage, median_ms, p95_ms, rows_seen as records_the_reader_sees
from results
order by reader, query, case storage when 'none' then 0 when 'kernel table' then 1 else 2 end;

-- Why: the plans of the list page for the first reader. The kernel table's "no tags, or a shared tag" is two subqueries
-- joined by OR, which the planner runs as hashed subplans over every tag row of the record type before the first row
-- of the page; the column is two conditions on the row itself, so the page walks the sort's index and stops at 50.
\echo
\echo The plans of the list page, every unit and one tag: the kernel table, then the column.
select format(
    'explain (analyze, costs off, timing off, summary off) select r.id from kernel_sample.measured_records r where r.owning_organisation_id = any(%L::uuid[]) and (not exists (select 1 from kernel_data.record_tags t where t.record_type = ''measured_record'' and t.record_id = r.id) or exists (select 1 from kernel_data.record_tags t where t.record_type = ''measured_record'' and t.record_id = r.id and t.tag_code = any(%L::text[]))) order by r.name limit 50',
    units, held) as joined,
    format(
    'explain (analyze, costs off, timing off, summary off) select r.id from kernel_sample.measured_records r where r.owning_organisation_id = any(%L::uuid[]) and (cardinality(r.visibility_tags) = 0 or r.visibility_tags && %L::text[]) order by r.name limit 50',
    units, held) as columned
from readers where name = 'every unit, one tag' \gset
:joined;
:columned;
