using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using IdentityModel;
using IdentityModel.Client;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace ParClientWithStockHandler
{
    public class ParEvents : OpenIdConnectEvents
    {
        private readonly HttpClient _httpClient;
        private const string HeaderValueEpocDate = "Thu, 01 Jan 1970 00:00:00 GMT";

        public ParEvents(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        public override async Task RedirectToIdentityProvider(RedirectContext context)
        {
            // ******************************************************************
            // This is all copied from the handler, except for one section below
            // ******************************************************************

            // Save client id, we will need that in our par request
            var clientId = context.ProtocolMessage.ClientId;

            // Construct State, we also need that (this chunk copied from the OIDC handler)
            var message = context.ProtocolMessage;
            // When redeeming a code for an AccessToken, this value is needed
            context.Properties.Items.Add(OpenIdConnectDefaults.RedirectUriForCodePropertiesKey, message.RedirectUri);
            message.State = context.Options.StateDataFormat.Protect(context.Properties);


            // **********************************
            // This is the bit that isn't copied
            // **********************************

            // Send our PAR request
            var requestBody = new FormUrlEncodedContent(context.ProtocolMessage.Parameters);
            _httpClient.SetBasicAuthentication(clientId, "secret");
            
            var disco = await _httpClient.GetDiscoveryDocumentAsync("https://localhost:5001");
            if(disco.IsError)
            {
                throw new Exception(disco.Error);
            }
            var parEndpoint = disco.TryGetValue("pushed_authorization_request_endpoint").GetString();
            var response = await _httpClient.PostAsync(parEndpoint, requestBody);
            if(!response.IsSuccessStatusCode)
            {
                throw new Exception("PAR failure");
            }
            var par = await response.Content.ReadFromJsonAsync<ParResponse>();
            
            // Remove all the parameters from the protocol message, and replace with what we got from the PAR response
            context.ProtocolMessage.Parameters.Clear();
            // Then, set client id and request uri as parameters
            context.ProtocolMessage.ClientId = clientId;
            context.ProtocolMessage.RequestUri = par.RequestUri;

            // ****************************************************************
            // Mark the request as handled, because we don't want the normal
            // behavior that attaches state to the outgoing request (we already
            // did that in the PAR request). 
            // ****************************************************************
            context.HandleResponse();

            // However, we do want all the rest of the normal behavior, so the below is copied from what the handler normally does after this event
            // https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs#L477-L511
            if (string.IsNullOrEmpty(message.IssuerAddress))
            {
                throw new InvalidOperationException(
                    "Cannot redirect to the authorization endpoint, the configuration may be missing or invalid.");
            }

            if (context.Options.AuthenticationMethod == OpenIdConnectRedirectBehavior.RedirectGet)
            {
                var redirectUri = message.CreateAuthenticationRequestUrl();
                if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                {
                    // TODO
                    // Logger.InvalidAuthenticationRequestUrl(redirectUri);
                }

                context.Response.Redirect(redirectUri);
                return;
            }
            else if (context.Options.AuthenticationMethod == OpenIdConnectRedirectBehavior.FormPost)
            {
                var content = message.BuildFormPost();
                var buffer = Encoding.UTF8.GetBytes(content);

                context.Response.ContentLength = buffer.Length;
                context.Response.ContentType = "text/html;charset=UTF-8";

                // Emit Cache-Control=no-cache to prevent client caching.
                context.Response.Headers.CacheControl = "no-cache, no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = HeaderValueEpocDate;

                await context.Response.Body.WriteAsync(buffer);
                return;
            }

            throw new NotImplementedException($"An unsupported authentication method has been configured: {context.Options.AuthenticationMethod}");

        }

        public override Task TokenResponseReceived(TokenResponseReceivedContext context)
        {
            return base.TokenResponseReceived(context);
        }

        private class ParResponse
        {
            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("request_uri")]
            public string RequestUri { get; set; }
        }
    }
}