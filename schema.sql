create table if not exists garmin_activity (
    activity_id   bigint primary key,
    start_time    timestamp not null,
    sport         text,
    name          text,
    distance_km   numeric(8,2),
    elapsed_time  text,
    moving_time   text,
    avg_speed     numeric(6,2),
    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now()
);
