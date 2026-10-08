using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;
using WalletLedger.Api.Application;
using WalletLedger.Api.Domain;
using WalletLedger.Api.Infrastructure.Database;

var builder = WebApplication.CreateBuilder(args);
// Register the API surface, database connection factory and transaction service.
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddHealthChecks();

var app = builder.Build();
// Convert known domain failures into stable HTTP problem responses.
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        ValidationException => (StatusCodes.Status422UnprocessableEntity, "Validation failed"),
        NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
        InsufficientFundsException => (StatusCodes.Status409Conflict, "Insufficient funds"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
    };
    context.Response.StatusCode = status;
    await Results.Problem(title: title, detail: exception?.Message).ExecuteAsync(context);
}));

if (app.Environment.IsDevelopment())
{
    // Keep interactive API documentation out of production by default.
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.Title = "Wallet Ledger API");
}

app.MapHealthChecks("/health").WithTags("Operations");

// Account endpoints create the account and expose its current read model.
app.MapPost("/accounts", async (CreateAccountRequest request, LedgerService service, CancellationToken cancellationToken) =>
    Results.Created($"/accounts/{request.OwnerId}", await service.CreateAccountAsync(request, cancellationToken)))
    .WithTags("Accounts");

app.MapGet("/accounts/{accountId:guid}/balance", async (Guid accountId, LedgerService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetBalanceAsync(accountId, cancellationToken)))
    .WithTags("Accounts");

app.MapGet("/accounts/{accountId:guid}/entries", async (Guid accountId, long? beforeId, int? limit, LedgerService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetEntriesAsync(accountId, beforeId, limit ?? 20, cancellationToken)))
    .WithTags("Ledger");

// Money-moving endpoints require an idempotency key so retries cannot duplicate a transaction.
app.MapPost("/topups", async (HttpRequest httpRequest, TopUpRequest request, LedgerService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.TopUpAsync(ReadIdempotencyKey(httpRequest), request, cancellationToken)))
    .WithTags("Transactions");

app.MapPost("/transfers", async (HttpRequest httpRequest, TransferRequest request, LedgerService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.TransferAsync(ReadIdempotencyKey(httpRequest), request, cancellationToken)))
    .WithTags("Transactions");

app.Run();

static string ReadIdempotencyKey(HttpRequest request) => request.Headers.TryGetValue("Idempotency-Key", out var value)
    ? value.ToString()
    : throw new ValidationException("Idempotency-Key header is required.");

public partial class Program
{
}
