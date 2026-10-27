using System.Text.Json;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Roller.MockPartner;

/// <summary>Reads the drill switches at most every few seconds per execution environment.</summary>
internal sealed class Switches(IAmazonSimpleSystemsManagement ssm, string parameterName, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private PartnerSwitches _current = PartnerSwitches.Healthy;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async Task<PartnerSwitches> CurrentAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow() - _readAt < CacheFor)
        {
            return _current;
        }

        var response = await ssm.GetParameterAsync(new GetParameterRequest { Name = parameterName }, ct);
        _current = JsonSerializer.Deserialize<PartnerSwitches>(response.Parameter.Value, PartnerJson.Options)
            ?? throw new InvalidDataException($"{parameterName} is empty");
        _readAt = clock.GetUtcNow();
        return _current;
    }
}
