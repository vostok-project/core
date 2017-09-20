﻿using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Client.Requests;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Client.Response;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Common;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Common.Headers;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Common.HttpContent;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.Common.Utility;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.ToCore.Collections;
using Vostok.ClusterClient.Transport.Http.Vostok.Http.ToCore.Utilities.Convertions.Time;
using Vostok.Commons.Collections;
using Vostok.Logging;
using HttpMethod = Vostok.ClusterClient.Transport.Http.Vostok.Http.Common.HttpMethod;
using HttpResponseHeaders = Vostok.ClusterClient.Transport.Http.Vostok.Http.Client.Headers.HttpResponseHeaders;

namespace Vostok.ClusterClient.Transport.Http.Vostok.Http.Client
{
    // ReSharper disable MethodSupportsCancellation

    public class HttpClient : IHttpClient
	{
		public HttpClient(HttpClientSettings settings, ILog log)
		{
			Preconditions.EnsureNotNull(settings, "settings");
			Preconditions.EnsureNotNull(log, "log");
			this.settings = settings;
		    this.log = log;

		    threadPoolMonitor = ThreadPoolMonitor.Instance;
		}

	    public Task<HttpResponse> SendAsync(HttpRequest request, TimeSpan timeout)
	    {
            return SendAsync(request, timeout, CancellationToken.None);
	    }

	    public async Task<HttpResponse> SendAsync(HttpRequest request, TimeSpan timeout, CancellationToken cancellationToken)
		{
                if (timeout.TotalMilliseconds < 1)
                {
                    LogRequestTimeout(request, timeout);
                    return new HttpResponse(HttpResponseCode.RequestTimeout);
                }

                var state = new HttpWebRequestState(timeout);

		        using (var timeoutCancellation = new CancellationTokenSource())
		        {
		            var timeoutTask = Task.Delay(state.TimeRemaining, timeoutCancellation.Token);
		            var senderTask = SendInternalAsync(request, state, cancellationToken);
		            var completedTask = await Task.WhenAny(timeoutTask, senderTask).ConfigureAwait(false);//?

		            if (completedTask is Task<HttpResponse> taskWithResponse)
		            {
		                timeoutCancellation.Cancel();
		                return taskWithResponse.GetAwaiter().GetResult();
		            }

		            // (iloktionov): Если выполнившееся задание не кастуется к Task<HttpResponse>, сработал таймаут.
		            state.CancelRequest();
		            LogRequestTimeout(request, timeout);
		            threadPoolMonitor.ReportAndFixIfNeeded(log);

		            // (iloktionov): Попытаемся дождаться завершения задания по отправке запроса перед тем, как возвращать результат:
		            await Task.WhenAny(senderTask.ContinueWith(_ => { }), Task.Delay(RequestAbortTimeout)).ConfigureAwait(false);

		            if (!senderTask.IsCompleted)
		                LogFailedToWaitForRequestAbort();

		            return BuildFailureResponse(HttpResponseCode.RequestTimeout, state);
		        }
		}

		private async Task<HttpResponse> SendInternalAsync(HttpRequest request, HttpWebRequestState state, CancellationToken cancellationToken)
		{
		    using (cancellationToken.Register(state.CancelRequest))
		    {
		        for (state.ConnectionAttempt = 1; state.ConnectionAttempt <= settings.ConnectionAttempts; state.ConnectionAttempt++)
		            using (state)
		            {
                        if (state.RequestCancelled)
                            return new HttpResponse(HttpResponseCode.Canceled);

                        state.Reset();
		                state.Request = HttpWebRequestFactory.Create(request, settings, state.TimeRemaining);

		                HttpActionStatus status;

                        // (iloktionov): Шаг 1 - отправить тело запроса, если оно имеется.
                        if (state.RequestCancelled)
                            return new HttpResponse(HttpResponseCode.Canceled);
                        if (request.Body != null)
		                {
		                    status = await LimitConnectTime(SendRequestBodyAsync(request, state), request, state).ConfigureAwait(false);
		                    if (status == HttpActionStatus.ConnectionFailure)
		                        continue;
		                    if (status != HttpActionStatus.Success)
		                        return BuildFailureResponse(status, state);
		                }

                        // (iloktionov): Шаг 2 - получить ответ от сервера.
                        if (state.RequestCancelled)
                            return new HttpResponse(HttpResponseCode.Canceled);
		                if (request.Body != null) // (krait): Если получилось отправить тело запроса, проверять подключение уже не нужно.
		                    status = await GetResponseAsync(request, state).ConfigureAwait(false);
		                else
		                    status = await LimitConnectTime(GetResponseAsync(request, state), request, state).ConfigureAwait(false);
		                if (status == HttpActionStatus.ConnectionFailure)
		                    continue;
		                if (status != HttpActionStatus.Success)
		                    return BuildFailureResponse(status, state);

		                // (iloktionov): Шаг 3 - скачать тело ответа, если оно имеется.
		                if (!NeedToReadResponseBody(request, state))
		                    return BuildSuccessResponse(state);

                        if (state.RequestCancelled)
                            return new HttpResponse(HttpResponseCode.Canceled);

		                status = await ReadResponseBodyAsync(request, state).ConfigureAwait(false);
		                return status == HttpActionStatus.Success
		                    ? BuildSuccessResponse(state)
		                    : BuildFailureResponse(status, state);
		            }

		        return new HttpResponse(HttpResponseCode.ConnectFailure);
		    }
		}

		private Task<HttpActionStatus> LimitConnectTime(Task<HttpActionStatus> mainTask, HttpRequest request, HttpWebRequestState state)
		{
            if (!settings.UseConnectTimeout)
                return mainTask;

            if (!ConnectTimeoutHelper.CanCheckSocket)
                return mainTask; 

            if (state.TimeRemaining < settings.ConnectTimeout)
                return mainTask;

            if (request.AbsoluteUri.IsLoopback)
                return mainTask;

            if (ConnectTimeoutHelper.IsSocketConnected(state.Request, log))
                return mainTask;

            return LimitConnectTimeInternal(mainTask, request, state);
		}

        private async Task<HttpActionStatus> LimitConnectTimeInternal(Task<HttpActionStatus> mainTask, HttpRequest request, HttpWebRequestState state)
        {
            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var completedTask = await Task.WhenAny(mainTask, Task.Delay(settings.ConnectTimeout, timeoutCancellation.Token)).ConfigureAwait(false);

                if (completedTask is Task<HttpActionStatus> taskWithResult)
                {
                    timeoutCancellation.Cancel();
                    return taskWithResult.GetAwaiter().GetResult();
                }

                if (!ConnectTimeoutHelper.IsSocketConnected(state.Request, log))
                {
                    state.CancelRequestAttempt();
                    LogConnectionFailure(request, new WebException($"Connection attempt timed out. Timeout = {settings.ConnectTimeout}.", WebExceptionStatus.ConnectFailure), state.ConnectionAttempt);
                    return HttpActionStatus.ConnectionFailure;
                }

                return await mainTask.ConfigureAwait(false);
            }
        }

        private async Task<HttpActionStatus> SendRequestBodyAsync(HttpRequest request, HttpWebRequestState state)
		{
			try
			{
				state.RequestStream = await state.Request.GetRequestStreamAsync().ConfigureAwait(false);
			}
			catch (WebException error)
			{
				return HandleWebException(request, state, error);
			}
			catch (Exception error)
			{
				LogUnknownException(error);
				return HttpActionStatus.UnknownFailure;
			}

			try
			{
				await request.Body.CopyToAsync(state.RequestStream).ConfigureAwait(false);
				state.CloseRequestStream();
			}
			catch (Exception error)
			{
                if (IsCancellationException(error))
                    return HttpActionStatus.RequestCanceled;

                LogSendBodyFailure(request, error);
				return HttpActionStatus.SendFailure;
			}

			return HttpActionStatus.Success;
		}

		private async Task<HttpActionStatus> GetResponseAsync(HttpRequest request, HttpWebRequestState state)
		{
			try
			{
				state.Response = (HttpWebResponse) await state.Request.GetResponseAsync().ConfigureAwait(false);
				state.ResponseStream = state.Response.GetResponseStream();
				return HttpActionStatus.Success;
			}
			catch (WebException error)
			{
				var status = HandleWebException(request, state, error);
				// (iloktionov): HttpWebRequest реагирует на коды ответа вроде 404 или 500 исключением со статусом ProtocolError.
				if (status == HttpActionStatus.ProtocolError)
				{
					state.Response = (HttpWebResponse) error.Response;
					state.ResponseStream = state.Response.GetResponseStream();
					return HttpActionStatus.Success;
				}
				return status;
			}	
			catch (Exception error)
			{
				LogUnknownException(error);
				return HttpActionStatus.UnknownFailure;
			}	
		}

		private static bool NeedToReadResponseBody(HttpRequest request, HttpWebRequestState state)
		{
			if (request.Method == HttpMethod.HEAD)
				return false;
			// (iloktionov): ContentLength может быть равен -1, если сервер не укажет заголовок, но вернет контент. Это умолчание на уровне HttpWebRequest.
			return state.Response.ContentLength != 0;
		}

		private async Task<HttpActionStatus> ReadResponseBodyAsync(HttpRequest request, HttpWebRequestState state)
		{
			try
			{
				var contentLength = (int) state.Response.ContentLength;
				if (contentLength > 0)
				{
                    state.BodyBuffer = new byte[contentLength];

                    var totalBytesRead = 0;

                    // (iloktionov): Если буфер размером contentLength не попадет в LOH, можно передать его напрямую для работы с сокетом.
                    // В противном случае лучше использовать небольшой промежуточный буфер из пула, т.к. ссылка на переданный сохранится надолго из-за Keep-Alive.
                    if (contentLength < LOHObjectSizeThreshold)
				    {
				        while (totalBytesRead < contentLength)
				        {
				            var bytesToRead = Math.Min(contentLength - totalBytesRead, PreferredReadSize);
				            var bytesRead = await state.ResponseStream.ReadAsync(state.BodyBuffer, totalBytesRead, bytesToRead).ConfigureAwait(false);
				            if (bytesRead == 0)
				                break;
                            
				            totalBytesRead += bytesRead;
				        }
				    }
				    else
				    {
				        using (var bufferHandle = ReadBuffersPool.AcquireHandle())
				        {
				            var buffer = bufferHandle.Resource;

                            while (totalBytesRead < contentLength)
                            {
                                var bytesToRead = Math.Min(contentLength - totalBytesRead, buffer.Length);
                                var bytesRead = await state.ResponseStream.ReadAsync(buffer, 0, bytesToRead).ConfigureAwait(false);
                                if (bytesRead == 0)
                                    break;

                                Buffer.BlockCopy(buffer, 0, state.BodyBuffer, totalBytesRead, bytesRead);

                                totalBytesRead += bytesRead;
                            }
                        }
				    }

                    if (totalBytesRead < contentLength)
                        throw new EndOfStreamException(string.Format("Response stream ended prematurely. Read only {0} byte(s), but Content-Length specified {1}.", totalBytesRead, contentLength));
				}
				else
				{
					state.BodyStream = new MemoryStream();

                    using (var bufferHandle = ReadBuffersPool.AcquireHandle())
                    {
                        var buffer = bufferHandle.Resource;

                        while (true)
                        {
                            var bytesRead = await state.ResponseStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            if (bytesRead == 0)
                                break;

                            state.BodyStream.Write(buffer, 0, bytesRead);
                        }
                    }
				}

				return HttpActionStatus.Success;
			}
			catch (Exception error)
			{
                if (IsCancellationException(error))
                    return HttpActionStatus.RequestCanceled;

				LogReceiveBodyFailure(request, error);
				return HttpActionStatus.ReceiveFailure;
			}
		}

        private HttpActionStatus HandleWebException(HttpRequest request, HttpWebRequestState state, WebException error)
		{
			switch (error.Status)
			{
				case WebExceptionStatus.ConnectFailure:
				case WebExceptionStatus.KeepAliveFailure:
				case WebExceptionStatus.ConnectionClosed:
				case WebExceptionStatus.PipelineFailure:
				case WebExceptionStatus.NameResolutionFailure:
				case WebExceptionStatus.ProxyNameResolutionFailure:
				case WebExceptionStatus.SecureChannelFailure:
					LogConnectionFailure(request, error, state.ConnectionAttempt);
					return HttpActionStatus.ConnectionFailure;
				case WebExceptionStatus.SendFailure:
					LogWebException(error);
					return HttpActionStatus.SendFailure;
				case WebExceptionStatus.ReceiveFailure:
					LogWebException(error);
					return HttpActionStatus.ReceiveFailure;
				case WebExceptionStatus.RequestCanceled: return HttpActionStatus.RequestCanceled;
				case WebExceptionStatus.Timeout: return HttpActionStatus.Timeout;
				case WebExceptionStatus.ProtocolError: return HttpActionStatus.ProtocolError;
				default:
					LogWebException(error);
					return HttpActionStatus.UnknownFailure;
			}
		}

	    private bool IsCancellationException(Exception error)
	    {
	        return error is OperationCanceledException || (error as WebException)?.Status == WebExceptionStatus.RequestCanceled;
	    }

		private HttpResponse BuildSuccessResponse(HttpWebRequestState state)
		{
			return BuildResponseInternal(ResponseCodeUtilities.Convert((int)state.Response.StatusCode, log), state);
		}

		private static HttpResponse BuildFailureResponse(HttpActionStatus status, HttpWebRequestState state)
		{
			switch (status)
			{
				case HttpActionStatus.SendFailure: return BuildFailureResponse(HttpResponseCode.SendFailure, state);
				case HttpActionStatus.ReceiveFailure: return BuildFailureResponse(HttpResponseCode.ReceiveFailure, state);
				case HttpActionStatus.Timeout: return BuildFailureResponse(HttpResponseCode.RequestTimeout, state);
				case HttpActionStatus.RequestCanceled: return BuildFailureResponse(HttpResponseCode.Canceled, state);
				default: return BuildFailureResponse(HttpResponseCode.UnknownFailure, state);
			}
		}

		private static HttpResponse BuildFailureResponse(HttpResponseCode code, HttpWebRequestState state)
		{
			return state.Response == null 
				? new HttpResponse(code) 
				: BuildResponseInternal(code, state);
		}

		private static HttpResponse BuildResponseInternal(HttpResponseCode code, HttpWebRequestState state)
		{
			var response = state.Response;
			if (response == null)
				return new HttpResponse(code);

            var headers = new HttpResponseHeaders(response.Headers);

            var body = CreateResponseContent(state);
			if (body != null)
			{
				if (!string.IsNullOrEmpty(response.ContentType))
				{
				    body.ContentType = ContentType.Parse(response.ContentType, out var charset);
					body.Charset = charset;
				}
				else
				{
					body.ContentType = ContentType.OctetStream;
					body.Charset = EncodingFactory.GetDefault();
				}

				if (!string.IsNullOrEmpty(headers[HttpHeaderNames.ContentRange]))
					body.ContentRange = ContentRangeHeaderValue.Parse(headers[HttpHeaderNames.ContentRange]);
			}

            return new HttpResponse(code, headers, body, response.ProtocolVersion);
		}

	    private static ByteArrayContent CreateResponseContent(HttpWebRequestState state)
	    {
	        if (state.BodyBuffer != null)
	        {
	            return new ByteArrayContent(state.BodyBuffer);
	        }

	        if (state.BodyStream != null)
	        {
	            return new ByteArrayContent(state.BodyStream.GetBuffer(), 0, (int) state.BodyStream.Position);
	        }

	        return null;
	    }

		#region Logging

		private void LogRequestTimeout(HttpRequest request, TimeSpan timeout)
		{
            log.Error("[HttpClient] Request timed out. Target = {0}. Timeout = {1:0.000} sec.", request.AbsoluteUri.Authority, timeout.TotalSeconds);
		}

		private void LogConnectionFailure(HttpRequest request, WebException error, int attempt)
		{
            log.Error($"[HttpClient] Connection failure. Target = {request.AbsoluteUri.Authority}. Attempt = {attempt}/{settings.ConnectionAttempts}. Status = {error.Status}.", ExceptionUtility.UnwrapWebException(error));
		}

		private void LogWebException(WebException error)
		{
            log.Error($"[HttpClient] Error in sending request. Status = {error.Status}.", ExceptionUtility.UnwrapWebException(error));
		}

		private void LogUnknownException(Exception error)
		{
            log.Error("[HttpClient] Unknown error in sending request.", error);
		}

		private void LogSendBodyFailure(HttpRequest request, Exception error)
		{
            log.Error("[HttpClient] Error in sending request body to " + request.AbsoluteUri.Authority, error);
		}

		private void LogReceiveBodyFailure(HttpRequest request, Exception error)
		{
            log.Error("[HttpClient] Error in receiving request body from " + request.AbsoluteUri.Authority, error);
		}

        private void LogFailedToWaitForRequestAbort()
        {
            log.Warn("[HttpClient] Timed out request was aborted but did not complete in {0}.", RequestAbortTimeout);
        }

		#endregion

		#region HttpActionStatus
		private enum HttpActionStatus
		{
			Success,
			ConnectionFailure,
			SendFailure,
			ReceiveFailure,
			Timeout,
			RequestCanceled,
			ProtocolError,
			UnknownFailure
		} 
		#endregion

	    private readonly ThreadPoolMonitor threadPoolMonitor;
	    private readonly HttpClientSettings settings;
	    private readonly ILog log;

		private const int PreferredReadSize = 16 * 1024;
		private const int LOHObjectSizeThreshold = 85 * 1000;

        private static readonly TimeSpan RequestAbortTimeout = 250.Milliseconds();
        private static readonly Pool<byte[]> ReadBuffersPool = new Pool<byte[]>(() => new byte[PreferredReadSize]);
	}

    // ReSharper restore MethodSupportsCancellation
}