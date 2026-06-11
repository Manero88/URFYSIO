using System.Net.Http.Headers;
using Auth0.OidcClient;

namespace URFYSIO.App.Services;

/// <summary>
/// DelegatingHandler that attaches the Auth0 access token from SecureStorage to every outgoing
/// HTTP request and triggers Auth0 re-login when a 401 response is received.
/// </summary>
public class AuthTokenHandler : DelegatingHandler
{
    private static volatile bool _isRedirecting;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await SecureStorage.Default.GetAsync("auth_token");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !IsAuthEndpoint(request.RequestUri)
            && !_isRedirecting)
        {
            _isRedirecting = true;
            try
            {
                SecureStorage.Default.Remove("auth_token");
                SecureStorage.Default.Remove("auth_role");
                SecureStorage.Default.Remove("auth_userid");
                SecureStorage.Default.Remove("auth_username");

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Shell.Current.GoToAsync("//login");
                    _isRedirecting = false;
                });
            }
            catch
            {
                _isRedirecting = false;
            }
        }

        return response;
    }

    private static bool IsAuthEndpoint(Uri? uri) =>
        uri?.AbsolutePath.Contains("/api/registration", StringComparison.OrdinalIgnoreCase) == true;
}
