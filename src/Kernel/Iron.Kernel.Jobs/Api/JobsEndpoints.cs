using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Jobs.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Jobs.Api;

/// <summary>A run as the console shows it: its key and state, attempts, times, error, who started it and why, and its log when one run is asked for.</summary>
public sealed record JobRunResponse(
    Guid Id,
    string Job,
    string Key,
    string Status,
    PriorityClass Priority,
    int Attempts,
    DateTimeOffset ScheduledFor,
    DateTimeOffset DueAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    string? Error,
    Guid? TriggeredBy,
    string? Reason,
    DateTimeOffset? DeadAt,
    int Reruns,
    string? Log)
{
    public static JobRunResponse From(RunView run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new JobRunResponse(run.Id, run.Job, run.Key, run.Status, run.Priority, run.Attempts, run.ScheduledFor, run.DueAt, run.StartedAt, run.FinishedAt, run.DurationMs, run.Error, run.TriggeredBy, run.Reason, run.DeadAt, run.Reruns, run.Log);
    }
}

/// <summary>A job as the console lists it (TF-121): what it is, how it runs, its state, next and last runs, last failure, typical duration and backlog.</summary>
public sealed record JobResponse(
    string Name,
    string Module,
    string? Suite,
    string Description,
    string Owner,
    string Schedule,
    JobScope Scope,
    PriorityClass Priority,
    int TimeoutSeconds,
    int Attempts,
    int MaxParallel,
    string UniqueBy,
    string State,
    DateTimeOffset? PausedAt,
    Guid? PausedBy,
    string? PauseReason,
    DateTimeOffset? NextRunAt,
    JobRunResponse? LastRun,
    JobRunResponse? LastFailure,
    int? TypicalDurationMs,
    int Queued,
    int Running,
    int DeadLetters)
{
    public static JobResponse From(JobView job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new JobResponse(
            job.Name, job.Module, job.Suite, job.Description, job.Owner, job.Schedule, job.Scope, job.Priority, job.TimeoutSeconds, job.Attempts, job.MaxParallel, job.UniqueBy,
            job.State, job.PausedAt, job.PausedBy, job.PauseReason, job.NextRunAt,
            job.LastRun is { } last ? JobRunResponse.From(last) : null,
            job.LastFailure is { } failure ? JobRunResponse.From(failure) : null,
            job.TypicalDurationMs, job.Queued, job.Running, job.DeadLetters);
    }
}

/// <summary>A dead letter of a job (run again by its run's id) or of an event subscription (replayed to the subscriber), newest first.</summary>
public sealed record DeadLetterResponse(string Kind, Guid Id, string Name, string What, int Attempts, string? Error, DateTimeOffset DeadAt);

/// <summary>Why a person pauses, resumes, starts or runs a job again: the audit trail keeps it with their name.</summary>
public sealed record JobReasonRequest(string Reason);

public sealed class JobReasonRequestValidator : AbstractValidator<JobReasonRequest>
{
    public JobReasonRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(JobConsole.ReasonMaxLength);
    }
}

/// <summary>
/// The job console's API (TF-121, ADR-0061): the jobs with their runs, logs and backlog, read with <c>jobs.jobs.read</c>;
/// pause, resume, run now and run a dead letter again with <c>jobs.jobs.manage</c>, a recent sign-in and a reason, each
/// audited; and one list of the dead letters of jobs and, for a person who may read events, of event subscriptions.
/// </summary>
internal static class JobsEndpoints
{
    public static readonly ListFields<JobRun> RunFields = new ListFields<JobRun>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("status", r => r.Status, FieldUse.Select | FieldUse.Filter)
        .Add("key", r => r.Key, FieldUse.Select | FieldUse.Filter)
        .Add("attempts", r => r.Attempts, FieldUse.Select | FieldUse.Sort)
        .Add("scheduled_for", r => r.ScheduledFor, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("started_at", r => r.StartedAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("finished_at", r => r.FinishedAt, FieldUse.Select | FieldUse.Sort)
        .Add("duration_ms", r => r.DurationMs, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("scheduled_for", SortDirection.Descending);

    /// <summary>The one view of dead letters pages by the time each became one, newest first.</summary>
    public static readonly ListFields<JobRun> DeadFields = new ListFields<JobRun>()
        .Add("dead_at", r => r.DeadAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("dead_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var jobs = endpoints.MapApiGroup("jobs");
        jobs.MapGet("/", ListAsync).RequirePermission(JobsPermissions.JobsRead).Produces<IReadOnlyList<JobResponse>>(StatusCodes.Status200OK).WithName("ListJobs").WithSummary("Every job of this unit: its schedule, state, next and last runs, last failure, typical duration and backlog.");
        jobs.MapGet("/{name}", GetAsync).RequirePermission(JobsPermissions.JobsRead).Produces<JobResponse>(StatusCodes.Status200OK).WithName("GetJob").WithSummary("One job with its state, runs and backlog.");
        jobs.MapGet("/{name}/runs", RunsAsync).RequirePermission(JobsPermissions.JobsRead).Produces<ListResponse<JobRunResponse>>(StatusCodes.Status200OK).WithName("ListJobRuns").WithSummary("A job's runs, newest first, with their status, attempts, times and errors.");
        jobs.MapGet("/runs/{id:guid}", RunAsync).RequirePermission(JobsPermissions.JobsRead).Produces<JobRunResponse>(StatusCodes.Status200OK).WithName("GetJobRun").WithSummary("One run with its log and its last error.");
        jobs.MapPost("/{name}/pause", PauseAsync).RequirePermission(JobsPermissions.JobsManage).RequireRecentAuthentication().Validate<JobReasonRequest>().Produces<JobResponse>(StatusCodes.Status200OK).WithName("PauseJob").WithSummary("Pause a job, with a reason: it queues no run until it is resumed.");
        jobs.MapPost("/{name}/resume", ResumeAsync).RequirePermission(JobsPermissions.JobsManage).RequireRecentAuthentication().Validate<JobReasonRequest>().Produces<JobResponse>(StatusCodes.Status200OK).WithName("ResumeJob").WithSummary("Resume a paused job, with a reason: it falls due by its schedule from now.");
        jobs.MapPost("/{name}/run", TriggerAsync).RequirePermission(JobsPermissions.JobsManage).RequireRecentAuthentication().Validate<JobReasonRequest>().Produces<JobRunResponse>(StatusCodes.Status200OK).WithName("RunJobNow").WithSummary("Run a job now, with a reason.");
        jobs.MapPost("/runs/{id:guid}/rerun", RerunAsync).RequirePermission(JobsPermissions.JobsManage).RequireRecentAuthentication().Validate<JobReasonRequest>().Produces<JobRunResponse>(StatusCodes.Status200OK).WithName("RerunJobRun").WithSummary("Run a dead letter again, with a reason.");

        var dead = endpoints.MapApiGroup("dead-letters");
        dead.MapGet("/", DeadLettersAsync).RequirePermission(JobsPermissions.JobsRead).Produces<ListResponse<DeadLetterResponse>>(StatusCodes.Status200OK).WithName("ListAllDeadLetters").WithSummary("The dead letters of jobs and of event subscriptions together, newest first.");
    }

    private static async Task<IResult> ListAsync(JobConsole console, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<JobResponse>>([.. (await console.ListAsync(cancellationToken)).Select(JobResponse.From)]);

    private static async Task<IResult> GetAsync(string name, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.GetAsync(name, cancellationToken), http, job => TypedResults.Ok(JobResponse.From(job)));

    private static async Task<IResult> RunsAsync(string name, HttpContext http, JobsDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, RunFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Runs.AsNoTracking().Where(r => r.Job == name).ApplyListQuery(query, RunFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(r => JobRunResponse.From(RunView.Of(r, false))), http, query);
    }

    private static async Task<IResult> RunAsync(Guid id, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.RunAsync(id, cancellationToken), http, run => TypedResults.Ok(JobRunResponse.From(run)));

    private static async Task<IResult> PauseAsync(string name, JobReasonRequest request, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.PauseAsync(name, request.Reason, null, cancellationToken), http, job => TypedResults.Ok(JobResponse.From(job)));

    private static async Task<IResult> ResumeAsync(string name, JobReasonRequest request, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.ResumeAsync(name, request.Reason, null, cancellationToken), http, job => TypedResults.Ok(JobResponse.From(job)));

    private static async Task<IResult> TriggerAsync(string name, JobReasonRequest request, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.TriggerAsync(name, request.Reason, null, cancellationToken), http, run => TypedResults.Ok(JobRunResponse.From(run)));

    private static async Task<IResult> RerunAsync(Guid id, JobReasonRequest request, HttpContext http, JobConsole console, CancellationToken cancellationToken) =>
        ApiResults.From(await console.RerunAsync(id, request.Reason, null, cancellationToken), http, run => TypedResults.Ok(JobRunResponse.From(run)));

    /// <summary>The dead letters of jobs and, for a person who may also read events, of event subscriptions: one page of both, newest first.</summary>
    private static async Task<IResult> DeadLettersAsync(HttpContext http, JobConsole console, ICurrentPrincipal actor, IPermissionEvaluator permissions, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, DeadFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var readsEvents = (await permissions.ExplainAsync(new AccessRequest(actor.Id, EventsPermissions.EventsRead, "GET", http.Request.Path), cancellationToken)).Allowed;
        var page = await console.DeadLettersAsync(parsed.Value.Page, readsEvents, cancellationToken);
        return ApiResults.List(page.Map(d => new DeadLetterResponse(d.Kind, d.Id, d.Name, d.What, d.Attempts, d.Error, d.DeadAt)), http, parsed.Value);
    }
}
