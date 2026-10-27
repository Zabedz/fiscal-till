// CDK entry point: reads the alert email from context and defines the RollerGuardrails and RollerTill
// stacks in eu-west-2. cdk.json runs it with dotnet run after publishing the Lambdas.

using Amazon.CDK;
using Roller.Infra;
using CdkEnvironment = Amazon.CDK.Environment;

var app = new App();

var alertEmail = app.Node.TryGetContext("alertEmail") as string
    ?? throw new InvalidOperationException("Context value alertEmail is missing: add it to cdk.context.json or pass -c alertEmail=<address>.");

var env = new CdkEnvironment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = "eu-west-2",
};

new GuardrailsStack(app, "RollerGuardrails", new GuardrailsStackProps
{
    Env = env,
    Description = "Kept across till teardowns: monthly cost budget, alarm topic, SES sender identity.",
    TerminationProtection = true,
    AlertEmail = alertEmail,
    MonthlyBudgetUsd = 10,
});

new TillStack(app, "RollerTill", new TillStackProps
{
    Env = env,
    Description = "Till pipeline: API, sales table, fiscalisation and journal queues, receipts bucket.",
    Venues = VenueConfig.Load("config/venues.json"),
    AllowedOrigin = "http://localhost:4200",
});

app.Synth();
