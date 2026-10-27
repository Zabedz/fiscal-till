// RollerTill stack, removed by cdk destroy RollerTill: REST API with per-venue keys, the sale and fiscalise
// Lambdas, sales table, fiscalise and journal queues with DLQs, receipts bucket, DLQ alarms and the dashboard.

using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SecretsManager;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
using Constructs;
using Roller.Core;
using DynamoAttribute = Amazon.CDK.AWS.DynamoDB.Attribute;

namespace Roller.Infra;

public sealed class TillStackProps : StackProps
{
    public required IReadOnlyList<Venue> Venues { get; init; }
    public required string AllowedOrigin { get; init; }
    public IReadOnlyList<DrillWindow> Drills { get; init; } = [];
}

public sealed class TillStack : Stack
{
    // One partner attempt per delivery with a 3 s HTTP timeout; batches of 10 run in parallel.
    private static readonly Duration WorkerTimeout = Duration.Seconds(15);

    // Lambda's SQS guidance: visibility timeout at least 6x the function timeout.
    private static readonly Duration FiscaliseVisibility = Duration.Seconds(WorkerTimeout.ToSeconds() * 6);

    // Worker backoff (5 s doubling, 60 s cap, plus jitter) puts the last retry at least 6 minutes after the first delivery.
    private const int FiscaliseMaxReceives = 10;

    private const int JournalMaxReceives = 5;

    // Room for the latency drill (switch latencyMs up to about 10 s).
    private static readonly Duration PartnerTimeout = Duration.Seconds(15);

    // The worker has at most 50 partner calls in flight; the cap keeps a flood on the public URL off the account pool.
    private const int PartnerConcurrency = 60;

    private const string PartnerSwitchesParameter = "/roller/partner/switches";
    private const int SecretLength = 40;

    // 5 invocations of up to 10 messages each: at most 50 partner calls in flight when a backlog drains.
    private const int MaxWorkerInvocations = 5;

    private const double VenueRateLimit = 50;
    private const int VenueBurstLimit = 100;

    // Per key per day; at REST API prices the three venue keys cost at most about $3 a day.
    private const double VenueDailyQuota = 300_000;

    public TillStack(Construct scope, string id, TillStackProps props) : base(scope, id, props)
    {
        var sales = new Table(this, "Sales", new TableProps
        {
            PartitionKey = new DynamoAttribute { Name = "SaleId", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        sales.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "ByVenueDay",
            PartitionKey = new DynamoAttribute { Name = "VenueDay", Type = AttributeType.STRING },
            SortKey = new DynamoAttribute { Name = "CreatedAt", Type = AttributeType.STRING },
        });

        var fiscaliseDlq = new Queue(this, "FiscaliseDlq", new QueueProps
        {
            RetentionPeriod = Duration.Days(14),
            EnforceSSL = true,
        });
        var fiscaliseQueue = new Queue(this, "FiscaliseQueue", new QueueProps
        {
            VisibilityTimeout = FiscaliseVisibility,
            RetentionPeriod = Duration.Days(14),
            EnforceSSL = true,
            DeadLetterQueue = new DeadLetterQueue { Queue = fiscaliseDlq, MaxReceiveCount = FiscaliseMaxReceives },
        });

        // FIFO with one message group per till: the journal takes one till's sales one at a time.
        var journalDlq = new Queue(this, "JournalDlq", new QueueProps
        {
            Fifo = true,
            RetentionPeriod = Duration.Days(14),
            EnforceSSL = true,
        });
        var journalQueue = new Queue(this, "JournalQueue", new QueueProps
        {
            Fifo = true,
            VisibilityTimeout = Duration.Seconds(60),
            RetentionPeriod = Duration.Days(14),
            EnforceSSL = true,
            DeadLetterQueue = new DeadLetterQueue { Queue = journalDlq, MaxReceiveCount = JournalMaxReceives },
        });

        var receipts = new Bucket(this, "Receipts", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        // Gives the bucket's auto-delete Lambda a log group that cdk destroy removes.
        var autoDeleteLogs = new LogGroup(this, "AutoDeleteObjectsLogs", new LogGroupProps
        {
            Retention = RetentionDays.ONE_WEEK,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        var autoDeleteHandler = (Amazon.CDK.CfnResource)Node.FindChild("Custom::S3AutoDeleteObjectsCustomResourceProvider").Node.FindChild("Handler");
        autoDeleteHandler.AddPropertyOverride("LoggingConfig.LogGroup", autoDeleteLogs.LogGroupName);

        var api = new RestApi(this, "TillApi", new RestApiProps
        {
            RestApiName = "roller-till",
            CloudWatchRole = false,
            DeployOptions = new StageOptions
            {
                StageName = "live",
                ThrottlingRateLimit = 200,
                ThrottlingBurstLimit = 400,
            },
            DefaultCorsPreflightOptions = new CorsOptions
            {
                AllowOrigins = [props.AllowedOrigin],
                AllowMethods = ["GET", "POST"],
                AllowHeaders = [.. Cors.DEFAULT_HEADERS, "Idempotency-Key"],
            },
        });

        // CORS headers on API Gateway's own 4xx/5xx (429 throttle, 403 bad key), so the browser can read them.
        var corsHeaders = new Dictionary<string, string>
        {
            ["Access-Control-Allow-Origin"] = $"'{props.AllowedOrigin}'",
            ["Access-Control-Allow-Headers"] = "'Content-Type,X-Api-Key,Idempotency-Key'",
        };
        api.AddGatewayResponse("Default4xx", new GatewayResponseOptions { Type = ResponseType_.DEFAULT_4XX, ResponseHeaders = corsHeaders });
        api.AddGatewayResponse("Default5xx", new GatewayResponseOptions { Type = ResponseType_.DEFAULT_5XX, ResponseHeaders = corsHeaders });

        var plan = api.AddUsagePlan("VenuePlan", new UsagePlanProps
        {
            Name = "roller-venues",
            Throttle = new ThrottleSettings { RateLimit = VenueRateLimit, BurstLimit = VenueBurstLimit },
            Quota = new QuotaSettings { Limit = VenueDailyQuota, Period = Period.DAY },
        });
        plan.AddApiStage(new UsagePlanPerApiStage { Stage = api.DeploymentStage });

        // RestApi.AddApiKey binds the key to the stage, which depends on the Lambdas reading the key ids: a cycle.
        var keyIdByVenue = new Dictionary<string, string>();
        foreach (var venue in props.Venues)
        {
            var key = new ApiKey(this, $"{venue.VenueId}-key", new ApiKeyProps { ApiKeyName = $"roller-{venue.VenueId}" });
            plan.AddApiKey(key);
            keyIdByVenue[venue.VenueId] = key.KeyId;
        }
        var venueKeys = ToJsonString(keyIdByVenue);

        var createSale = NewFunction("CreateSale", "Roller.Api", "Roller.Api.CreateSaleFunction::Handle", Duration.Seconds(10), new()
        {
            ["SALES_TABLE"] = sales.TableName,
            ["FISCALISE_QUEUE_URL"] = fiscaliseQueue.QueueUrl,
            ["VENUE_API_KEYS"] = venueKeys,
            ["ALLOWED_ORIGIN"] = props.AllowedOrigin,
        });
        sales.Grant(createSale, "dynamodb:PutItem", "dynamodb:GetItem");
        fiscaliseQueue.GrantSendMessages(createSale);

        var getSale = NewFunction("GetSale", "Roller.Api", "Roller.Api.GetSaleFunction::Handle", Duration.Seconds(10), new()
        {
            ["SALES_TABLE"] = sales.TableName,
            ["VENUE_API_KEYS"] = venueKeys,
            ["ALLOWED_ORIGIN"] = props.AllowedOrigin,
        });
        sales.Grant(getSale, "dynamodb:GetItem");

        var partnerTable = new Table(this, "PartnerSignatures", new TableProps
        {
            PartitionKey = new DynamoAttribute { Name = "Pk", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        var partnerApiKey = NewSecret("PartnerApiKey");
        var partnerSigningKey = NewSecret("PartnerSigningKey");
        var partnerSwitches = new StringParameter(this, "PartnerSwitches", new StringParameterProps
        {
            ParameterName = PartnerSwitchesParameter,
            StringValue = """{"latencyMs":0,"errorRate":0,"outage":false}""",
        });

        var partner = NewFunction("MockPartner", "Roller.MockPartner", "Roller.MockPartner.PartnerFunction::Handle", PartnerTimeout, new()
        {
            ["PARTNER_TABLE"] = partnerTable.TableName,
            ["API_KEY_SECRET_ARN"] = partnerApiKey.SecretArn,
            ["SIGNING_KEY_SECRET_ARN"] = partnerSigningKey.SecretArn,
            ["SWITCHES_PARAMETER"] = partnerSwitches.ParameterName,
        }, PartnerConcurrency);
        partnerTable.Grant(partner, "dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:ConditionCheckItem");
        partnerApiKey.GrantRead(partner);
        partnerSigningKey.GrantRead(partner);
        partnerSwitches.GrantRead(partner);
        var partnerUrl = partner.AddFunctionUrl(new FunctionUrlOptions { AuthType = FunctionUrlAuthType.NONE });

        var worker = NewFunction("FiscaliseWorker", "Roller.Fiscalise", "Roller.Fiscalise.FiscaliseFunction::Handle", WorkerTimeout, new()
        {
            ["SALES_TABLE"] = sales.TableName,
            ["FISCALISE_QUEUE_URL"] = fiscaliseQueue.QueueUrl,
            ["JOURNAL_QUEUE_URL"] = journalQueue.QueueUrl,
            ["PARTNER_URL"] = partnerUrl.Url,
            ["PARTNER_KEY_SECRET_ARN"] = partnerApiKey.SecretArn,
        });
        sales.Grant(worker, "dynamodb:GetItem", "dynamodb:UpdateItem");
        journalQueue.GrantSendMessages(worker);
        partnerApiKey.GrantRead(worker);
        worker.AddEventSource(new SqsEventSource(fiscaliseQueue, new SqsEventSourceProps
        {
            BatchSize = 10,
            ReportBatchItemFailures = true,
            MaxConcurrency = MaxWorkerInvocations,
        }));

        var sale = api.Root.AddResource("sales");
        sale.AddMethod("POST", new LambdaIntegration(createSale), new MethodOptions { ApiKeyRequired = true });
        sale.AddResource("{saleId}").AddMethod("GET", new LambdaIntegration(getSale), new MethodOptions { ApiKeyRequired = true });

        var alarmTopic = Topic.FromTopicArn(this, "AlarmTopic", Arn.Format(new ArnComponents
        {
            Service = "sns",
            Resource = GuardrailsStack.AlarmTopicName,
        }, this));
        Alarm[] alarms = [AlarmWhenNotEmpty("FiscaliseDlq", fiscaliseDlq, alarmTopic), AlarmWhenNotEmpty("JournalDlq", journalDlq, alarmTopic)];

        TillDashboard.Add(this, new DashboardParts(
            api, createSale, worker, partner, fiscaliseQueue, fiscaliseDlq, journalQueue, journalDlq, alarms, props.Drills));

        new CfnOutput(this, "ApiUrl", new CfnOutputProps { Value = api.Url });
        new CfnOutput(this, "ApiKeyIds", new CfnOutputProps { Value = venueKeys });
        new CfnOutput(this, "SalesTableName", new CfnOutputProps { Value = sales.TableName });
        new CfnOutput(this, "FiscaliseQueueUrl", new CfnOutputProps { Value = fiscaliseQueue.QueueUrl });
        new CfnOutput(this, "FiscaliseDlqUrl", new CfnOutputProps { Value = fiscaliseDlq.QueueUrl });
        new CfnOutput(this, "JournalQueueUrl", new CfnOutputProps { Value = journalQueue.QueueUrl });
        new CfnOutput(this, "JournalDlqUrl", new CfnOutputProps { Value = journalDlq.QueueUrl });
        new CfnOutput(this, "ReceiptsBucketName", new CfnOutputProps { Value = receipts.BucketName });
        new CfnOutput(this, "PartnerUrl", new CfnOutputProps { Value = partnerUrl.Url });
    }

    private Secret NewSecret(string id) => new(this, id, new SecretProps
    {
        GenerateSecretString = new SecretStringGenerator { ExcludePunctuation = true, PasswordLength = SecretLength },
        RemovalPolicy = RemovalPolicy.DESTROY,
    });

    private Function NewFunction(
        string id, string project, string handler, Duration timeout, Dictionary<string, string> environment, int? reservedConcurrency = null) =>
        new(this, id, new FunctionProps
        {
            ReservedConcurrentExecutions = reservedConcurrency,
            Runtime = Runtime.DOTNET_10,
            Architecture = Architecture.ARM_64,
            MemorySize = 1024,
            Timeout = timeout,
            Code = Code.FromAsset($"artifacts/{project}"),
            Handler = $"{project}::{handler}",
            Environment = environment,
            LoggingFormat = LoggingFormat.JSON,
            // The default log group is retained on destroy and kept for two years.
            LogGroup = new LogGroup(this, $"{id}Logs", new LogGroupProps
            {
                Retention = RetentionDays.ONE_WEEK,
                RemovalPolicy = RemovalPolicy.DESTROY,
            }),
        });

    private Alarm AlarmWhenNotEmpty(string id, Queue dlq, ITopic topic)
    {
        var alarm = new Alarm(this, $"{id}NotEmpty", new AlarmProps
        {
            AlarmDescription = $"{id} has messages: inspect them, fix the cause, then redrive.",
            Metric = dlq.MetricApproximateNumberOfMessagesVisible(new MetricOptions
            {
                Period = Duration.Minutes(1),
                Statistic = Stats.MAXIMUM,
            }),
            Threshold = 0,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
            EvaluationPeriods = 1,
            TreatMissingData = TreatMissingData.NOT_BREACHING,
        });
        alarm.AddAlarmAction(new SnsAction(topic));
        return alarm;
    }
}
