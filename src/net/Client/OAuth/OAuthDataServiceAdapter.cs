﻿//-----------------------------------------------------------------------
// <copyright file="OAuthDataServiceAdapter.cs" company="Microsoft">Copyright 2012 Microsoft Corporation</copyright>
// <license>
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </license>


using System;
using System.Collections.Specialized;
using System.Data.Services.Client;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Practices.TransientFaultHandling;

namespace Microsoft.WindowsAzure.MediaServices.Client.OAuth
{

    /// <summary>
    /// An OAuth adapter for a data service.
    /// </summary>
    public class OAuthDataServiceAdapter
    {
        private const string AuthorizationHeader = "Authorization";
        private const string BearerTokenFormat = "Bearer {0}";
        private const string GrantType = "client_credentials";
        private const int ExpirationTimeBufferInSeconds = 120;  // The OAuth2 token expires in 10 hours, 
                                                                // so setting the buffer as 2 minutes is safe for 
                                                                // the network latency and clock skew.

        private readonly string _acsBaseAddress;
        private readonly string _trustedRestCertificateHash;
        private readonly string _trustedRestSubject;
        private readonly string _clientSecret;
        private readonly string _clientId;
        private readonly string _scope;

        private DateTime _tokenExpiration;

        /// <summary>
        /// Initializes a new instance of the <see cref="OAuthDataServiceAdapter"/> class.
        /// </summary>
        /// <param name="clientId">The client id.</param>
        /// <param name="clientSecret">The client secret.</param>
        /// <param name="scope">The scope.</param>
        /// <param name="acsBaseAddress">The acs base address.</param>
        /// <param name="trustedRestCertificateHash">The trusted rest certificate hash.</param>
        /// <param name="trustedRestSubject">The trusted rest subject.</param>
        public OAuthDataServiceAdapter(string clientId, string clientSecret, string scope, string acsBaseAddress, string trustedRestCertificateHash, string trustedRestSubject)
        {
            this._clientId = clientId;
            this._clientSecret = clientSecret;
            this._scope = scope;
            this._acsBaseAddress = acsBaseAddress;
            this._trustedRestCertificateHash = trustedRestCertificateHash;
            this._trustedRestSubject = trustedRestSubject;

            #if DEBUG
            ServicePointManager.ServerCertificateValidationCallback = this.ValidateCertificate;
            #endif

            this.GetToken();
        }

        /// <summary> 
        /// Gets OAuth Access Token to be used for web requests.
        /// </summary> 
        public string AccessToken { get; private set; }

        /// <summary>
        /// Adapts the specified data service context.
        /// </summary>
        /// <param name="dataServiceContext">The data service context.</param>
        public void Adapt(DataServiceContext dataServiceContext)
        {
            dataServiceContext.SendingRequest += this.OnSendingRequest;
        }

        /// <summary>
        /// Adds the access token to request.
        /// </summary>
        /// <param name="request">The request.</param>
        public void AddAccessTokenToRequest(WebRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (request.Headers[AuthorizationHeader] == null)
            {
                if (DateTime.Now > this._tokenExpiration)
                {
                    this.GetToken();
                }

                request.Headers.Add(AuthorizationHeader, string.Format(CultureInfo.InvariantCulture, BearerTokenFormat, this.AccessToken));
            }
        }

        private bool ValidateCertificate(object s, X509Certificate cert, X509Chain chain, SslPolicyErrors error)
        {
            if (error.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || error.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            {

                // This is for local deployments. DevFabric generates its own certificate for load-balancing / port forwarding.
                const string AzureDevFabricCertificateSubject = "CN=127.0.0.1, O=TESTING ONLY, OU=Windows Azure DevFabric";
                if (cert.Subject == AzureDevFabricCertificateSubject)
                {
                    return true;
                }
                var cert2 = new X509Certificate2(cert);
                if (this._trustedRestSubject == cert2.Subject && cert2.Thumbprint == this._trustedRestCertificateHash)
                {
                    return true;
                }
            }

            return error == SslPolicyErrors.None;
        }

        private void GetToken()
        {
            using (WebClient client = new WebClient())
            {
                client.BaseAddress = this._acsBaseAddress;

                var oauthRequestValues = new NameValueCollection
                {
                    {"grant_type", GrantType},
                    {"client_id", this._clientId},
                    {"client_secret", this._clientSecret},
                    {"scope", this._scope},
                };

                RetryPolicy retryPolicy = new RetryPolicy(
                    new WebRequestTransientErrorDetectionStrategy(),
                    RetryStrategyFactory.DefaultStrategy());

                retryPolicy.ExecuteAction(
                    () =>
                        {
                            byte[] responseBytes = client.UploadValues("/v2/OAuth2-13", "POST", oauthRequestValues);

                            using (var responseStream = new MemoryStream(responseBytes))
                            {
                                OAuth2TokenResponse tokenResponse = (OAuth2TokenResponse)new DataContractJsonSerializer(typeof (OAuth2TokenResponse)).ReadObject(responseStream);
                                this.AccessToken = tokenResponse.AccessToken;
                                this._tokenExpiration = DateTime.Now.AddSeconds(tokenResponse.ExpirationInSeconds - ExpirationTimeBufferInSeconds);
                            }
                        });
            }
        }

        /// <summary> 
        /// When sending Http Data requests to the Azure Marketplace, inject authorization header based on the current Access token.
        /// </summary> 
        /// <param name="sender">Event sender.</param> 
        /// <param name="e">Event arguments.</param> 
        private void OnSendingRequest(object sender, SendingRequestEventArgs e)
        {
            this.AddAccessTokenToRequest(e.Request);
        }
    }
}
