// Local journal worker: drains the FIFO journal queue into SQL Server, writes receipts to S3 and emails them with SES.
// Start it with scripts/journal.sh after scripts/journal-db.sh up.

using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SimpleEmailV2;
using Amazon.SQS;
using Roller.Core;
using Roller.Journal;

var builder = Host.CreateApplicationBuilder(args);
var settings = JournalSettings.From(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(VenueDirectory.Load());
builder.Services.AddSingleton(MarketRulesRegistry.Load());
builder.Services.AddSingleton<IAmazonSQS>(new AmazonSQSClient());
builder.Services.AddSingleton<ISaleStore>(new DynamoSaleStore(new AmazonDynamoDBClient(), settings.SalesTable));
builder.Services.AddSingleton<IJournalDb>(new SqlJournalDb(settings.ConnectionString));
builder.Services.AddSingleton<IReceiptStore>(new S3ReceiptStore(new AmazonS3Client(), settings.ReceiptsBucket));
builder.Services.AddSingleton<IReceiptMailer>(new SesReceiptMailer(new AmazonSimpleEmailServiceV2Client(), settings.ReceiptSender));
builder.Services.AddSingleton<JournalProcessor>();
builder.Services.AddHostedService<JournalWorker>();

builder.Build().Run();
