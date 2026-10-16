using System.Resources;
using System.Text.Json.Serialization;

namespace Sts2.Devkit;

public sealed class SdkFault(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

public static class SdkErrors
{
    private static readonly ResourceManager Messages = new("Sts2.Devkit.Errors", typeof(SdkErrors).Assembly);
    private static readonly IReadOnlyDictionary<string, (int Code, string Status)> Registry =
        new Dictionary<string, (int, string)>
        {
            ["INVALID_REQUEST"] = (400, "INVALID_ARGUMENT"),
            ["SESSION_MISMATCH"] = (409, "FAILED_PRECONDITION"),
            ["PLUGIN_NOT_LOADED"] = (404, "NOT_FOUND"),
            ["RELOAD_FAILED"] = (409, "FAILED_PRECONDITION"),
            ["PLUGIN_ID_MISMATCH"] = (409, "FAILED_PRECONDITION"),
            ["SDK_BUSY"] = (409, "ABORTED"),
            ["INTERNAL_FAILURE"] = (500, "INTERNAL")
        };

    public static object Normalize(Exception exception)
    {
        var reason = exception is SdkFault fault && Registry.ContainsKey(fault.Reason)
            ? fault.Reason : "INTERNAL_FAILURE";
        var (code, status) = Registry[reason];
        return new
        {
            error = new
            {
                code,
                status,
                message = Messages.GetString(reason),
                details = new[] { new ErrorInformation("ErrorInfo", reason, "sts2.devkit") }
            }
        };
    }

    private sealed record ErrorInformation(
        [property: JsonPropertyName("@type")] string Type, string Reason, string Domain);
}
