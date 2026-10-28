// RollerTill's CloudWatch dashboard: a row of queues, a row of workers, a row for the API, with the drills marked on every graph.

using System.Globalization;
using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace Roller.Infra;

/// <summary>A drill's time window, from load/drills.json, which the load scripts write.</summary>
public sealed record DrillWindow(string Name, DateTimeOffset Start, DateTimeOffset End)
{
    public static IReadOnlyList<DrillWindow> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<DrillWindow>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []
            : [];
}

internal sealed record DashboardParts(
    RestApi Api,
    Function CreateSale,
    Function Worker,
    Function Partner,
    Queue FiscaliseQueue,
    Queue FiscaliseDlq,
    Queue JournalQueue,
    Queue JournalDlq,
    IReadOnlyList<Alarm> Alarms,
    IReadOnlyList<DrillWindow> Drills);

internal static class TillDashboard
{
    private const int Third = 8;
    private const int GraphHeight = 6;
    private const string DrillStart = "#2f5da8";
    private const string DrillEnd = "#8a96a3";
    private static readonly Duration Period = Duration.Minutes(1);

    public static Dashboard Add(Construct scope, DashboardParts parts)
    {
        var drillMarks = parts.Drills
            .SelectMany(drill => new[]
            {
                new VerticalAnnotation { Date = Iso(drill.Start), Label = drill.Name, Color = DrillStart },
                new VerticalAnnotation { Date = Iso(drill.End), Color = DrillEnd },
            })
            .ToArray();

        GraphWidget Graph(string title, IMetric[] left, IMetric[]? right = null, string leftLabel = "", string rightLabel = "") => new(new GraphWidgetProps
        {
            Title = title,
            Left = left,
            Right = right ?? [],
            Width = Third,
            Height = GraphHeight,
            Period = Period,
            LeftYAxis = new YAxisProps { Label = leftLabel, ShowUnits = false, Min = 0 },
            RightYAxis = new YAxisProps { Label = rightLabel, ShowUnits = false, Min = 0 },
            VerticalAnnotations = drillMarks,
            LegendPosition = LegendPosition.BOTTOM,
        });

        var dashboard = new Dashboard(scope, "Dashboard", new DashboardProps
        {
            DashboardName = "roller-till",
            DefaultInterval = Duration.Hours(3),
        });

        dashboard.AddWidgets(
            new TextWidget(new TextWidgetProps
            {
                Width = 16,
                Height = 3,
                Markdown = "## Till pipeline\n" +
                    "Tills post sales to the API; the fiscalise worker gets each one signed by the mock partner; the local journal worker " +
                    "numbers it in SQL Server. Blue lines mark the start of each drill, grey lines its end.",
            }),
            new AlarmStatusWidget(new AlarmStatusWidgetProps { Title = "Dead-letter alarms", Alarms = parts.Alarms.ToArray(), Width = Third, Height = 3 }));

        dashboard.AddWidgets(
            // A sale backing off after a failed signing is in flight, so only the not-visible count shows an outage's backlog.
            Graph("Sales waiting to be signed",
                [
                    parts.FiscaliseQueue.MetricApproximateNumberOfMessagesVisible(Options("Maximum", "Waiting")),
                    parts.FiscaliseQueue.MetricApproximateNumberOfMessagesNotVisible(Options("Maximum", "Backing off")),
                ],
                [parts.FiscaliseQueue.MetricApproximateAgeOfOldestMessage(Options("Maximum", "Oldest wait"))],
                "sales", "seconds"),
            Graph("Dead-letter queues",
                [
                    parts.FiscaliseDlq.MetricApproximateNumberOfMessagesVisible(Options("Maximum", "Fiscalise DLQ")),
                    parts.JournalDlq.MetricApproximateNumberOfMessagesVisible(Options("Maximum", "Journal DLQ")),
                ],
                leftLabel: "messages"),
            Graph("Signed sales waiting for the journal",
                [parts.JournalQueue.MetricApproximateNumberOfMessagesVisible(Options("Maximum", "Waiting"))],
                [parts.JournalQueue.MetricApproximateAgeOfOldestMessage(Options("Maximum", "Oldest wait"))],
                "sales", "seconds"));

        dashboard.AddWidgets(
            Graph("Fiscalise worker",
                [
                    parts.Worker.MetricInvocations(Options("Sum", "Invocations")),
                    parts.Worker.MetricErrors(Options("Sum", "Errors")),
                    parts.Worker.MetricThrottles(Options("Sum", "Throttles")),
                ],
                [parts.Worker.Metric("ConcurrentExecutions", Options("Maximum", "Concurrent"))],
                "per minute", "concurrent"),
            Graph("Fiscalise worker duration",
                [parts.Worker.MetricDuration(Options("p50", "p50")), parts.Worker.MetricDuration(Options("p95", "p95"))],
                leftLabel: "ms"),
            Graph("Mock signing partner",
                [parts.Partner.MetricInvocations(Options("Sum", "Calls")), parts.Partner.MetricErrors(Options("Sum", "Errors"))],
                [parts.Partner.MetricDuration(Options("p95", "Duration p95"))],
                "per minute", "ms"));

        dashboard.AddWidgets(
            Graph("Till API requests",
                [
                    parts.Api.MetricCount(Options("Sum", "Requests")),
                    parts.Api.MetricClientError(Options("Sum", "4xx")),
                    parts.Api.MetricServerError(Options("Sum", "5xx")),
                ],
                leftLabel: "per minute"),
            Graph("Till API latency",
                [parts.Api.MetricLatency(Options("p50", "p50")), parts.Api.MetricLatency(Options("p95", "p95"))],
                leftLabel: "ms"),
            Graph("CreateSale",
                [parts.CreateSale.MetricDuration(Options("p50", "Duration p50")), parts.CreateSale.MetricDuration(Options("p95", "Duration p95"))],
                [parts.CreateSale.Metric("ConcurrentExecutions", Options("Maximum", "Concurrent"))],
                "ms", "concurrent"));

        return dashboard;
    }

    private static MetricOptions Options(string statistic, string label) => new() { Statistic = statistic, Label = label, Period = Period };

    private static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
