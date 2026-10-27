using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace Roller.Journal;

internal interface IReceiptStore
{
    Task PutAsync(string key, string html, string saleId, CancellationToken ct);
}

/// <summary>Writes each receipt once; a key that already holds another sale's receipt is an error.</summary>
internal sealed class S3ReceiptStore(IAmazonS3 s3, string bucket) : IReceiptStore
{
    private const string SaleIdMetadata = "sale-id";

    public async Task PutAsync(string key, string html, string saleId, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = html,
            ContentType = "text/html; charset=utf-8",
            IfNoneMatch = "*",
        };
        request.Metadata.Add(SaleIdMetadata, saleId);

        try
        {
            await s3.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // An earlier delivery of the same sale wrote it already; another sale under this key means the journal was reset.
            var existing = await s3.GetObjectMetadataAsync(bucket, key, ct);
            var owner = existing.Metadata[SaleIdMetadata];
            if (owner != saleId)
            {
                throw new InvalidOperationException($"Receipt {key} is owned by sale {owner}; refusing sale {saleId}");
            }
        }
    }
}

internal interface IReceiptMailer
{
    Task SendAsync(string to, string subject, string html, CancellationToken ct);
}

internal sealed class SesReceiptMailer(IAmazonSimpleEmailServiceV2 ses, string sender) : IReceiptMailer
{
    public Task SendAsync(string to, string subject, string html, CancellationToken ct) =>
        ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = sender,
            Destination = new Destination { ToAddresses = [to] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body { Html = new Content { Data = html, Charset = "UTF-8" } },
                },
            },
        }, ct);
}
