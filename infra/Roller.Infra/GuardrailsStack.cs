// RollerGuardrails stack: monthly cost budget, alarm topic and SES sender identity. Kept when RollerTill is
// destroyed, so the email confirmations happen once.

using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.SES;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Roller.Infra;

public sealed class GuardrailsStackProps : StackProps
{
    public required string AlertEmail { get; init; }
    public required double MonthlyBudgetUsd { get; init; }
}

public sealed class GuardrailsStack : Stack
{
    // TillStack imports the topic by this name, so either stack can be destroyed alone.
    public const string AlarmTopicName = "roller-alarms";

    public GuardrailsStack(Construct scope, string id, GuardrailsStackProps props) : base(scope, id, props)
    {
        var alarms = new Topic(this, "AlarmTopic", new TopicProps
        {
            TopicName = AlarmTopicName,
            DisplayName = "Till alarms",
        });
        alarms.AddSubscription(new EmailSubscription(props.AlertEmail));

        // SES sandbox: the verified address is both sender and only allowed recipient.
        new EmailIdentity(this, "ReceiptSender", new EmailIdentityProps
        {
            Identity = Identity.Email(props.AlertEmail),
        });

        // Account-wide; budget data lags spend by hours, so teardown remains the real cost control.
        new CfnBudget(this, "MonthlyCost", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetName = "roller-monthly-cost",
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty { Amount = props.MonthlyBudgetUsd, Unit = "USD" },
            },
            NotificationsWithSubscribers = new[]
            {
                BudgetAlert("ACTUAL", 50, props.AlertEmail),
                BudgetAlert("FORECASTED", 100, props.AlertEmail),
            },
        });
    }

    private static CfnBudget.NotificationWithSubscribersProperty BudgetAlert(string type, double percent, string email) => new()
    {
        Notification = new CfnBudget.NotificationProperty
        {
            NotificationType = type,
            ComparisonOperator = "GREATER_THAN",
            Threshold = percent,
            ThresholdType = "PERCENTAGE",
        },
        Subscribers = new[] { new CfnBudget.SubscriberProperty { SubscriptionType = "EMAIL", Address = email } },
    };
}
