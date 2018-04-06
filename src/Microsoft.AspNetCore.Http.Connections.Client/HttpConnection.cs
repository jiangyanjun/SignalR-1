// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Connections.Client.Internal;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Connections.Internal;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Http.Connections.Client
{
    public partial class HttpConnection : IConnection
    {
        private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(120);
#if !NETCOREAPP2_1
        private static readonly Version Windows8Version = new Version(6, 2);
#endif

        private readonly ILogger _logger;

        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private bool _started;
        private bool _disposed;

        private readonly HttpClient _httpClient;
        private readonly HttpOptions _httpOptions;
        private ITransport _transport;
        private readonly ITransportFactory _transportFactory;
        private string _connectionId;
        private readonly HttpTransportType _requestedTransports;
        private readonly ConnectionLogScope _logScope;
        private readonly IDisposable _scopeDisposable;
        private readonly ILoggerFactory _loggerFactory;

        public Uri Url { get; }

        public IDuplexPipe Transport
        {
            get
            {
                CheckDisposed();
                if (_transport == null)
                {
                    throw new InvalidOperationException($"Cannot access the {nameof(Transport)} pipe before the connection has started.");
                }
                return _transport;
            }
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public HttpConnection(Uri url)
            : this(url, HttpTransports.All)
        { }

        public HttpConnection(Uri url, HttpTransportType transports)
            : this(url, transports, loggerFactory: null)
        {
        }

        public HttpConnection(Uri url, ILoggerFactory loggerFactory)
            : this(url, HttpTransports.All, loggerFactory, httpOptions: null)
        {
        }

        public HttpConnection(Uri url, HttpTransportType transports, ILoggerFactory loggerFactory)
            : this(url, transports, loggerFactory, httpOptions: null)
        {
        }

        public HttpConnection(Uri url, HttpTransportType transports, ILoggerFactory loggerFactory, HttpOptions httpOptions)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

            _logger = _loggerFactory.CreateLogger<HttpConnection>();
            _httpOptions = httpOptions;

            _requestedTransports = transports;
            if (_requestedTransports != HttpTransportType.WebSockets)
            {
                _httpClient = CreateHttpClient();
            }

            _transportFactory = new DefaultTransportFactory(transports, _loggerFactory, _httpClient, httpOptions);
            _logScope = new ConnectionLogScope();
            _scopeDisposable = _logger.BeginScope(_logScope);
        }

        public HttpConnection(Uri url, ITransportFactory transportFactory, ILoggerFactory loggerFactory, HttpOptions httpOptions)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<HttpConnection>();
            _httpOptions = httpOptions;
            _httpClient = CreateHttpClient();
            _requestedTransports = HttpTransports.All;
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _logScope = new ConnectionLogScope();
            _scopeDisposable = _logger.BeginScope(_logScope);
        }

        public Task StartAsync() => StartAsync(TransferFormat.Binary);

        public async Task StartAsync(TransferFormat transferFormat)
        {
            await StartAsyncCore(transferFormat).ForceAsync();
        }

        private async Task StartAsyncCore(TransferFormat transferFormat)
        {
            CheckDisposed();

            if (_started)
            {
                Log.SkippingStart(_logger);
                return;
            }

            await _connectionLock.WaitAsync();
            try
            {
                CheckDisposed();

                if (_started)
                {
                    Log.SkippingStart(_logger);
                    return;
                }

                Log.Starting(_logger);

                await SelectAndStartTransport(transferFormat);

                _started = true;
                Log.Started(_logger);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task DisposeAsync() => await DisposeAsyncCore().ForceAsync();

        private async Task DisposeAsyncCore(Exception exception = null)
        {
            if (_disposed)
            {
                return;
            }

            await _connectionLock.WaitAsync();
            try
            {
                if (!_disposed && _started)
                {
                    Log.DisposingHttpConnection(_logger);

                    // Stop the transport, but we don't care if it throws.
                    // The transport should also have completed the pipe with this exception.
                    try
                    {
                        await _transport.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.TransportThrewExceptionOnStop(_logger, ex);
                    }

                    Log.Disposed(_logger);
                }
                else
                {
                    Log.SkippingDispose(_logger);
                }

                _httpClient?.Dispose();
            }
            finally
            {
                // We want to do these things even if the WaitForWriterToComplete/WaitForReaderToComplete fails
                if (!_disposed)
                {
                    _scopeDisposable.Dispose();
                    _disposed = true;
                }

                _connectionLock.Release();
            }
        }

        private async Task SelectAndStartTransport(TransferFormat transferFormat)
        {
            if (_requestedTransports == HttpTransportType.WebSockets)
            {
                Log.StartingTransport(_logger, _requestedTransports, Url);
                await StartTransport(Url, _requestedTransports, transferFormat);
            }
            else
            {
                var negotiationResponse = await GetNegotiationResponse();

                // This should only need to happen once
                var connectUrl = CreateConnectUrl(Url, negotiationResponse.ConnectionId);

                // We're going to search for the transfer format as a string because we don't want to parse
                // all the transfer formats in the negotiation response, and we want to allow transfer formats
                // we don't understand in the negotiate response.
                var transferFormatString = transferFormat.ToString();

                foreach (var transport in negotiationResponse.AvailableTransports)
                {
                    if (!Enum.TryParse<HttpTransportType>(transport.Transport, out var transportType))
                    {
                        Log.TransportNotSupported(_logger, transport.Transport);
                        continue;
                    }

                    if (transportType == HttpTransportType.WebSockets && !IsWebSocketsSupported())
                    {
                        Log.WebSocketsNotSupportedByOperatingSystem(_logger);
                        continue;
                    }

                    try
                    {
                        if ((transportType & _requestedTransports) == 0)
                        {
                            Log.TransportDisabledByClient(_logger, transportType);
                        }
                        else if (!transport.TransferFormats.Contains(transferFormatString, StringComparer.Ordinal))
                        {
                            Log.TransportDoesNotSupportTransferFormat(_logger, transportType, transferFormat);
                        }
                        else
                        {
                            // The negotiation response gets cleared in the fallback scenario.
                            if (negotiationResponse == null)
                            {
                                negotiationResponse = await GetNegotiationResponse();
                                connectUrl = CreateConnectUrl(Url, negotiationResponse.ConnectionId);
                            }

                            Log.StartingTransport(_logger, transportType, connectUrl);
                            await StartTransport(connectUrl, transportType, transferFormat);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.TransportFailed(_logger, transportType, ex);
                        // Try the next transport
                        // Clear the negotiation response so we know to re-negotiate.
                        negotiationResponse = null;
                    }
                }
            }

            if (_transport == null)
            {
                throw new InvalidOperationException("Unable to connect to the server with any of the available transports.");
            }
        }

        private async Task<NegotiationResponse> Negotiate(Uri url, HttpClient httpClient, ILogger logger)
        {
            try
            {
                // Get a connection ID from the server
                Log.EstablishingConnection(logger, url);
                var urlBuilder = new UriBuilder(url);
                if (!urlBuilder.Path.EndsWith("/"))
                {
                    urlBuilder.Path += "/";
                }
                urlBuilder.Path += "negotiate";

                using (var request = new HttpRequestMessage(HttpMethod.Post, urlBuilder.Uri))
                {
                    // Corefx changed the default version and High Sierra curlhandler tries to upgrade request
                    request.Version = new Version(1, 1);

                    // ResponseHeadersRead instructs SendAsync to return once headers are read
                    // rather than buffer the entire response. This gives a small perf boost.
                    // Note that it is important to dispose of the response when doing this to
                    // avoid leaving the connection open.
                    using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        NegotiationResponse negotiateResponse;
                        using (var responseStream = await response.Content.ReadAsStreamAsync())
                        {
                            negotiateResponse = NegotiateProtocol.ParseResponse(responseStream);
                        }
                        Log.ConnectionEstablished(_logger, negotiateResponse.ConnectionId);
                        return negotiateResponse;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithNegotiation(logger, url, ex);
                throw;
            }
        }

        private static Uri CreateConnectUrl(Uri url, string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                throw new FormatException("Invalid connection id.");
            }

            return Utils.AppendQueryString(url, "id=" + connectionId);
        }

        private async Task StartTransport(Uri connectUrl, HttpTransportType transportType, TransferFormat transferFormat)
        {
            // Construct the transport
            var transport = _transportFactory.CreateTransport(transportType);

            // Start the transport, giving it one end of the pipe
            try
            {
                await transport.StartAsync(connectUrl, transferFormat);
            }
            catch (Exception ex)
            {
                Log.ErrorStartingTransport(_logger, transport, ex);

                _transport = null;
                throw;
            }

            if (transportType == HttpTransportType.LongPolling)
            {
                // Disable keep alives for long polling
                Features.Set<IConnectionInherentKeepAliveFeature>(new ConnectionInherentKeepAliveFeature(_httpClient.Timeout));
            }

            // We successfully started, set the transport properties (we don't want to set these until the transport is definitely running).
            _transport = transport;

            Log.TransportStarted(_logger, _transport);
        }

        private HttpClient CreateHttpClient()
        {
            var httpClientHandler = new HttpClientHandler();
            HttpMessageHandler httpMessageHandler = httpClientHandler;

            if (_httpOptions != null)
            {
                if (_httpOptions.Proxy != null)
                {
                    httpClientHandler.Proxy = _httpOptions.Proxy;
                }
                if (_httpOptions.Cookies != null)
                {
                    httpClientHandler.CookieContainer = _httpOptions.Cookies;
                }
                if (_httpOptions.ClientCertificates != null)
                {
                    httpClientHandler.ClientCertificates.AddRange(_httpOptions.ClientCertificates);
                }
                if (_httpOptions.UseDefaultCredentials != null)
                {
                    httpClientHandler.UseDefaultCredentials = _httpOptions.UseDefaultCredentials.Value;
                }
                if (_httpOptions.Credentials != null)
                {
                    httpClientHandler.Credentials = _httpOptions.Credentials;
                }

                httpMessageHandler = httpClientHandler;
                if (_httpOptions.HttpMessageHandlerFactory != null)
                {
                    httpMessageHandler = _httpOptions.HttpMessageHandlerFactory(httpClientHandler);
                    if (httpMessageHandler == null)
                    {
                        throw new InvalidOperationException("Configured HttpMessageHandlerFactory did not return a value.");
                    }
                }

                // Apply the authorization header in a handler instead of a default header because it can change with each request
                if (_httpOptions.AccessTokenFactory != null)
                {
                    httpMessageHandler = new AccessTokenHttpMessageHandler(httpMessageHandler, _httpOptions.AccessTokenFactory);
                }
            }

            // Wrap message handler after HttpMessageHandlerFactory to ensure not overriden
            httpMessageHandler = new LoggingHttpMessageHandler(httpMessageHandler, _loggerFactory);

            var httpClient = new HttpClient(httpMessageHandler);
            httpClient.Timeout = HttpClientTimeout;

            // Start with the user agent header
            httpClient.DefaultRequestHeaders.UserAgent.Add(Constants.UserAgentHeader);

            // Apply any headers configured on the HttpOptions
            if (_httpOptions?.Headers != null)
            {
                foreach (var header in _httpOptions.Headers)
                {
                    httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
            }

            httpClient.DefaultRequestHeaders.Remove("X-Requested-With");
            // Tell auth middleware to 401 instead of redirecting
            httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            return httpClient;
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HttpConnection));
            }
        }

        private static bool IsWebSocketsSupported()
        {
#if NETCOREAPP2_1
            // .NET Core 2.1 and above has a managed implementation
            return true;
#else
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!isWindows)
            {
                // Assume other OSes have websockets
                return true;
            }
            else
            {
                // Windows 8 and above has websockets
                return Environment.OSVersion.Version >= Windows8Version;
            }
#endif
        }

        private async Task<NegotiationResponse> GetNegotiationResponse()
        {
            var negotiationResponse = await Negotiate(Url, _httpClient, _logger);
            _connectionId = negotiationResponse.ConnectionId;
            _logScope.ConnectionId = _connectionId;
            return negotiationResponse;
        }
    }
}
