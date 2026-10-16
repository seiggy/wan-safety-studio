namespace VideoSafetyStudio.Server.Endpoints;

internal static class FormRequests
{
    public static async Task<IFormCollection> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            throw new BadHttpRequestException("Use multipart/form-data or application/x-www-form-urlencoded.", StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            throw new BadHttpRequestException("The form body is malformed.", StatusCodes.Status400BadRequest, error);
        }
    }
}
