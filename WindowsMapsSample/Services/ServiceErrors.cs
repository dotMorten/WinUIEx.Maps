using Azure;

namespace WindowsMapsSample.Services;

internal static class ServiceErrors
{
    internal static bool IsExpected(Exception exception) =>
        exception is RequestFailedException or HttpRequestException or OperationCanceledException
            or UnauthorizedAccessException or IOException or System.Text.Json.JsonException
            or InvalidDataException or ArgumentException;

    internal static string Describe(Exception exception) => exception switch
    {
        RequestFailedException { Status: 401 or 403 } =>
            "Azure Maps rejected this account key or access is restricted. Check the key and account permissions in Settings.",
        RequestFailedException { Status: 429 } =>
            "Azure Maps is busy or the account quota has been reached. Wait a moment and try again.",
        RequestFailedException { Status: 400 or 404 } =>
            "Azure Maps could not resolve this request. Try another location or travel mode.",
        OperationCanceledException => "The request timed out. Check your connection and try again.",
        UnauthorizedAccessException => "Access was denied. Check the app permissions in Windows Settings.",
        InvalidDataException => "Azure Maps returned incomplete route information. Try another route.",
        ArgumentException => "The route could not be displayed. Try choosing the endpoints again.",
        System.Text.Json.JsonException => "Azure Maps returned incomplete search information. Try again.",
        IOException => "Settings could not be saved. Check available storage and try again.",
        _ => "Azure Maps could not be reached. Check your connection and try again.",
    };
}
