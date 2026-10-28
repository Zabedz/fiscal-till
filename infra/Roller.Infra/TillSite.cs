// The till's public site: CloudFront serving the Angular build from a private bucket and passing /sales to the API, so
// the page and the API share one origin.

using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace Roller.Infra;

internal sealed record SiteParts(Bucket Bucket, Distribution Distribution);

internal static class TillSite
{
    public static SiteParts Add(Construct scope, RestApi api)
    {
        var bucket = new Bucket(scope, "Site", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        var distribution = new Distribution(scope, "SiteDistribution", new DistributionProps
        {
            Comment = "roller-till site",
            DefaultRootObject = "index.html",
            PriceClass = PriceClass.PRICE_CLASS_100,
            DefaultBehavior = new BehaviorOptions
            {
                Origin = S3BucketOrigin.WithOriginAccessControl(bucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                ResponseHeadersPolicy = ResponseHeadersPolicy.SECURITY_HEADERS,
            },
            AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
            {
                // The origin path is the API's stage, so /sales reaches /live/sales; every viewer header but Host goes
                // through, including x-api-key and Idempotency-Key.
                ["/sales*"] = new BehaviorOptions
                {
                    Origin = new RestApiOrigin(api),
                    ViewerProtocolPolicy = ViewerProtocolPolicy.HTTPS_ONLY,
                    AllowedMethods = AllowedMethods.ALLOW_ALL,
                    CachePolicy = CachePolicy.CACHING_DISABLED,
                    OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
                },
            },
        });

        return new SiteParts(bucket, distribution);
    }
}
