﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

namespace EmsApi.Client.V2
{
    /// <summary>
    /// Handles authentication and compression for API calls. This class will handle gzip headers
    /// and decompression, as well as requesting authentication tokens when necessary.
    /// </summary>
    /// <remarks>
    /// Because authentication is not attempted until the first time the service is accessed,
    /// we provide a callback for authentication errors instead of throwing an exception, since
    /// it can come at an unexpected time for the client.
    /// </remarks>
    internal class EmsApiClientHandler : HttpClientHandler, IDisposable
    {
        public EmsApiClientHandler()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip;

            m_endpoint = string.Empty;
            m_userName = string.Empty;
            m_pass = string.Empty;
        }

        private string m_authToken;
        private DateTime m_tokenExpiration;
        private EmsApiServiceConfiguration m_serviceConfig;
        private string m_endpoint, m_userName, m_pass;

        /// <summary>
        /// Returns true if the client is currently authenticated.
        /// </summary>
        public bool Authenticated { get; private set; }

        /// <summary>
        /// Sets the current service configuration, causing the authentication
        /// to become invalid if the endpoint, username, or password changed.
        /// </summary>
        public EmsApiServiceConfiguration ServiceConfig
        {
            set
            {
                bool deAuth = false;
                if( value.Endpoint != m_endpoint )
                {
                    m_endpoint = value.Endpoint;
                    deAuth = true;
                }
                if( value.UserName != m_userName )
                {
                    m_userName = value.UserName;
                    deAuth = true;
                }
                if( value.Password != m_pass )
                {
                    m_pass = value.Password;
                    deAuth = true;
                }

                if( deAuth )
                    InvalidateAuthentication();

                m_serviceConfig = value;
            }
        }
        
        /// <summary>
        /// Fired to signal that authentication has failed for the current request.
        /// </summary>
        public event EventHandler<AuthenticationFailedEventArgs> AuthenticationFailedEvent;

        /// <summary>
        /// Requests a new authentication token immediately.
        /// </summary>
        public bool Authenticate( CancellationToken? cancel = null )
        {
            try
            {
                return AuthenticateAsync( cancel ).Result;
            }
            catch( AggregateException ex )
            {
                // Rethrow aggregate exceptions.
                foreach( Exception inner in ex.InnerExceptions )
                    throw inner;

                return false;
            }
        }

        /// <summary>
        /// Requests a new authentication token immediately.
        /// </summary>
        public async Task<bool> AuthenticateAsync( CancellationToken? cancel = null )
        {
            if( IsTokenValid() )
                return true;

            // Use a semaphore so only a single task can authenticate at a time.
            var semaphore = new SemaphoreSlim( 1 );
            if( cancel != null )
                await semaphore.WaitAsync( cancel.Value );
            else
                await semaphore.WaitAsync();

            try
            {
                // We could have been beaten to the auth request.
                if( Authenticated )
                    return true;

                GetTokenResult result = await GetNewBearerToken( cancel );
                if( result.Success )
                {
                    Authenticated = true;
                    return true;
                }

                // Notify listerners of authentication failure.
                OnAuthenticationFailed( new AuthenticationFailedEventArgs( result.Error ) );
                return false;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void InvalidateAuthentication()
        {
            Authenticated = false;
            m_authToken = string.Empty;
            m_tokenExpiration = DateTime.MinValue;
        }

        private bool IsTokenValid()
        {
            return DateTime.UtcNow < m_tokenExpiration;
        }

        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            if( !IsTokenValid() )
            {
                // Even if we fail to authenticate, we need to send the request or 
                // other code might be stuck awaiting the send.
                if( !await AuthenticateAsync( cancellationToken ) )
                    return await base.SendAsync( request, cancellationToken );
            }

            // Apply our auth token to the header.
            request.Headers.Authorization = new AuthenticationHeaderValue( SecurityConstants.Scheme, m_authToken );
            return await base.SendAsync( request, cancellationToken );
        }

        private async Task<GetTokenResult> GetNewBearerToken( CancellationToken? cancel = null )
        {
            Authenticated = false;
            HttpRequestMessage request = new HttpRequestMessage( HttpMethod.Post, string.Format( "{0}/token", m_serviceConfig.Endpoint ) );
            m_serviceConfig.AddDefaultRequestHeaders( request.Headers );

            request.Content = new FormUrlEncodedContent( new Dictionary<string, string>
            {
                { "grant_type", SecurityConstants.GrantTypePassword },
                { "username", m_serviceConfig.UserName },
                { "password", m_serviceConfig.Password }
            } );

            CancellationToken cancelToken = cancel.HasValue ? cancel.Value : new CancellationToken();
            HttpResponseMessage response = base.SendAsync( request, cancelToken ).Result;

            // Regardless of if we succeed or fail the call, the returned structure will be a chunk of JSON.
            string rawResult = await response.Content.ReadAsStringAsync();
            JObject result = JObject.Parse( rawResult );

            if( !response.IsSuccessStatusCode )
            {
                string description = result.GetValue( "error_description" ).ToString();
                return GetTokenResult.Fail( string.Format( "Unable to retrieve EMS API bearer token: {0}", description ) );
            }

            string token = result.GetValue( "access_token" ).ToString();
            int expiresIn = result.GetValue( "expires_in" ).ToObject<int>();

            // Stash the new token and keep track of when we expire.
            m_authToken = token;
            m_tokenExpiration = DateTime.UtcNow.AddSeconds( expiresIn );
            return GetTokenResult.Succeed();
        }

        private void OnAuthenticationFailed( AuthenticationFailedEventArgs e )
        {
            if( AuthenticationFailedEvent != null )
                AuthenticationFailedEvent( this, e );
        }

        protected override void Dispose( bool disposing )
        {
            base.Dispose( disposing );
        }

        private class GetTokenResult
        {
            public static GetTokenResult Fail( string message )
            {
                var result = new GetTokenResult();
                result.Success = false;
                result.Error = message;
                return result;
            }

            public static GetTokenResult Succeed()
            {
                var result = new GetTokenResult();
                result.Success = true;
                return result;
            }

            public bool Success { get; set; }

            public string Error { get; set; }
        }

        private class SecurityConstants
        {
            public const string GrantTypePassword = "password";
            public const string GrantTypeTrusted = "trusted";
            public const string Scheme = "Bearer";
        }
    }
}
