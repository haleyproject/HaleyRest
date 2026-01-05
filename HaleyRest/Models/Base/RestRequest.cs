using Haley.Abstractions;
using Haley.Enums;
using Haley.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Models {
    //GET METHODS WITH A BODY: https://stackoverflow.com/questions/978061/http-get-with-request-body

    /// <summary>
    /// A simple straightforward HTTPClient Wrapper.
    /// </summary>
    public sealed class RestRequest : RestBase, IRequest {
        HttpRequestMessage _request = null;  //Prio-1
        HttpContent _content = null; //Prio-2
        List<IRequestContent> _requestObjects = new List<IRequestContent>();//Prio-3
        bool _inherit_headers = false;
        bool _inherit_authentication = false;
        bool _inherit_auth_param = false;
        bool _prevent_authentication = false;
        IProgressReporter _reporter = null;
        #region Attributes
        string _boundary = "----CustomBoundary" + DateTime.Now.Ticks.ToString("x");
        CancellationToken? _cancellation_token = null;
        HttpCompletionOption? _http_completion_option = null;
        public IClient Client { get; private set; }
        #endregion

        #region Constructors
        public RestRequest(string end_point_url, IClient client) : base(end_point_url) {
            Client = client;
        }
        public RestRequest(string end_point_url) : this(end_point_url, null) { }
        public RestRequest() : this(string.Empty, null) { }
        #endregion

        #region Request Creation
        public override IRequest WithQuery(IQueryRequestContent param) {
            return WithParameter(param);
        }
        public override IRequest WithQueries(IEnumerable<IQueryRequestContent> parameters) {
            return WithParameters(parameters);
        }
        public override IRequest WithBody(object content, bool is_serialized, BodyContentType content_type) {
            return WithParameter(new RawBodyRequestContent(content, is_serialized, content_type));
        }
        public override IRequest WithBody(IRawBodyRequestContent rawBodyRequest) {
            return WithParameter(rawBodyRequest);
        }

        public override IRequest WithParameter(IRequestContent param) {
            _requestObjects.Add(param);
            return this;
        }

        public override IRequest WithForm(IFormRequestContent param) {
            return WithParameter(param);
        }

        public override IRequest WithForm(Dictionary<string,object> param) {
            var form = new FormDataRequestContent(param);
            return WithParameter(form);
        }

        public override IRequest WithForm(FormUrlEncodedContent content, string key) {
            var form = new FormDataRequestContent(content,key);
            return WithParameter(form);
        }
        public override IRequest WithParameters(IEnumerable<IRequestContent> parameters) {
            _requestObjects.AddRange(parameters);
            return this;
        }
        public override IRequest WithContent(HttpContent content) {
            _content = content;
            return this;
        }
        #endregion

        #region Base Fluent Methods
        public IRequest DoNotAuthenticate() {
            _prevent_authentication = true;
            return this;
        }

        public IRequest SetClient(IClient client) {
            this.Client = client;
            return this;
        }
        public override IRequest WithEndPoint(string resource_url_endpoint) {
            if (Client != null && string.IsNullOrWhiteSpace(Client.URL)) {
                //Client's base URL is empty.
                //Do not try to set the clients url because it could be deliberate. In next call, endpoint might contain full URL again.
                URL = resource_url_endpoint; //Rest URL
                return this;
            }

            URL = ParseURI(resource_url_endpoint).pathQuery;
            return this;
        }

        public override IRequest AddCancellationToken(CancellationToken cancellation_token) {
            this._cancellation_token = cancellation_token;
            return this;
        }

        public override IRequest AddHTTPCompletion(HttpCompletionOption completion_option) {
            this._http_completion_option = completion_option;
            return this;
        }

        public override IRequest WithProgressReporter(IProgressReporter reporter) {
            _reporter = reporter;
            return this;
        }

        #endregion

        #region Get Methods
        public override async Task<RestResponse<T>> GetAsync<T>() {
            var _response = await GetAsync();
            var _options = GetSerializerOptions();
            var result = await new RestResponse<T>(_response.OriginalResponse)
                               .SetConveter((str) => { return JsonSerializer.Deserialize<T>(str, _options); })
                               .FetchContent();
            return result;
        }
        public override async Task<IResponse> GetAsync() {
            return await SendAsync(Method.GET);
        }
        public override async Task<IResponse> PostAsync() {
            return await SendAsync(Method.POST);
        }
        public override async Task<IResponse> PutAsync() {
            return await SendAsync(Method.PUT);
        }
        public override async Task<IResponse> DeleteAsync() {
            return await SendAsync(Method.DELETE);
        }
        public override async Task<IResponse> SendAsync(Method method) {
            ValidateClient();
            if (URL == null) URL = string.Empty;
            if (_request != null) {
                //Prio 1 : If request is available.
                return await SendAsync(_request);
            } else if (_content != null) {
                //Prio 2 : If content is availble without request.
                return await SendAsync(_content, URL, method); //Send associated URL without parsing
            } else if (_requestObjects != null && _requestObjects.Count > 0) {
                //Prio 3: Conver the request objects to httpcontent.
                var processedInputs = ConverToHttpContent(URL, _requestObjects, method); //Here, URL is just the end point.
                return await SendAsync(processedInputs.content, processedInputs.url, method);
            } else {
                //No content, no queries. Just send the plain request with the given method.
                return await SendAsync(null, URL, method);
            }
        }

        #endregion

        #region Send Methods

        private string GetAuthValue(IRestBase source, HttpRequestMessage request) {
            var authenticator = FetchAuthenticator(this);
            if (authenticator == null) return null;
            var authparam = FetchAuthParam(this);
            //When calling the authenticator from internally, let's also add url_decode because we know that we are already encoding (Uri.EscapeDataString) the query parameters (both Key and value).
            //"true" after authparam is for url_decode
            return authenticator?.GenerateToken(this.Client?.BaseClient?.BaseAddress, request, authparam) ?? String.Empty;
        }

        private IAuthProvider FetchAuthenticator(IRestBase source) {
            var authenticator = source.GetAuthenticator();
            if (authenticator != null) {
                return authenticator;
            } else if (source is RestRequest res_req && res_req._inherit_authentication && res_req.Client != null) {
                return FetchAuthenticator(res_req.Client);
            }
            return null;
        }

        private IAuthParam FetchAuthParam(IRestBase source) {
            var param = source.GetAuthParam();
            if (param != null) {
                return param;
            } else if (source is RestRequest res_req && res_req._inherit_auth_param && res_req.Client != null) {
                return FetchAuthParam(res_req.Client);
            }
            return null;
        }

        async Task<IResponse> SendAsync(HttpContent content, string url, Method method) {

            WriteLog(LogLevel.Information, $@"Initiating a {method} request to {url} with base url {Client.URL}");
            //1. Here, we do not add anything to the URL or Content.
            //2. We just validate the URl and get the path and query part.
            //3. Add request headers and Authentication (if available).
            HttpMethod request_method = HttpMethod.Get;
            switch (method) {
                case Method.GET:
                request_method = HttpMethod.Get;
                break;
                case Method.POST:
                request_method = HttpMethod.Post;
                break;
                case Method.DELETE:
                request_method = HttpMethod.Delete;
                break;
                case Method.PUT:
                request_method = HttpMethod.Put;
                break;
                case Method.PATCH:
                request_method = new HttpMethod("PATCH");
                break;
                case Method.HEAD:
                request_method = HttpMethod.Head;
                break;
            }
            //At this point, do not parse the URL. It might already contain the URL params added to it. So just call the URL. // parseURI(url).resource_part
            var uri_components = ParseURI(url);
            var resource_Url = uri_components.pathQuery;

            if (string.IsNullOrWhiteSpace(Client.URL)) { //if the client url is empty, then we conside the request url
                resource_Url = url; //Take the full url, irrespective of whatever is provided, assuming that the URL is absolute.
            }

            //SET REQUEST PROPERTY VALUE
            _request = new HttpRequestMessage(request_method, resource_Url);

            if (content != null) {
                _request.Content = content; //Set content if not null
            }

            #region Authentication and Headers
            var _headers = GetHeaders();
            if (_inherit_headers) {
                var _parentHeaders = Client?.GetHeaders();
                //Update values from here onto the _headers

            }

            if (_headers?.Count > 0) {
                foreach (var kvp in _headers) {
                    try {
                        _request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value); //Do not validate.
                    } catch (Exception ex) {
                        WriteLog(LogLevel.Debug, new EventId(2001, "Header Error"), "Error while trying to add a header", ex);
                    }
                }
            }
            #endregion

            var result = await SendAsync(method);
            return result; //All calls from here will receive stringResponse content.
        }

        internal async Task<IResponse> SendAsync(HttpRequestMessage request) {
            this._request = request;
            ValidateClient();
            HandleAuthorization();

            var _validationCB = Client.GetRequestValidation();

            //if some sort of validation callback is assigned, then call that first.
            if (_validationCB != null) {
                var validation_check = await _validationCB.Invoke(_request);
                if (!validation_check) {
                    WriteLog(LogLevel.Information, "Local request validation failed. Please verify the validation methods to return true on successful validation");
                    return new BaseResponse(null).SetMessage("Internal Request Validation call back failed.");
                }
            }

            //Here we donot modify anything. We just send and receive the response.
            HttpResponseMessage message;
            try {
                if (_cancellation_token != null && _http_completion_option != null) {
                    message = await Client.BaseClient.SendAsync(_request, _http_completion_option.Value, _cancellation_token.Value);
                } else if (_http_completion_option != null) {
                    message = await Client.BaseClient.SendAsync(_request, _http_completion_option.Value);
                } else if (_cancellation_token != null) {
                    message = await Client.BaseClient.SendAsync(_request, _cancellation_token.Value);
                } else {
                    message = await Client.BaseClient.SendAsync(_request);
                }
            } catch (Exception ex) {
                throw;
            }
            return new BaseResponse(message);
        }

        #endregion

        #region Helpers
        void HandleAuthorization() {
            //If Donot authenticate this request, then ignore all auth methods (may be the user has directly added the auth header)
            if (_prevent_authentication) return;

            string headerName = RestConstants.Headers.Authorization;
            var authenticator = FetchAuthenticator(this);
            if (authenticator == null) return; // no need to check further.

            if (authenticator is APIKeyProvider keyProvider) {
                headerName = keyProvider.GetKey();
            }

            //Authorization should happen only here because we would only add all query params before this stage.
            if (_request.Headers.Contains(headerName)) {
                _request.Headers.Remove(headerName);
            }

            //_request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", GetAuthValue(this, _request)); //if the input is not correct, for instance, 

            var authHeader = GetAuthValue(this, _request);

            if (!string.IsNullOrWhiteSpace(authHeader)) {
                _request.Headers.TryAddWithoutValidation(headerName, authHeader);
            }
        }
        private void ValidateClient() {
            if (Client == null) throw new ArgumentNullException(nameof(Client));
        }
        (HttpContent content, string url) ConverToHttpContent(string url, IEnumerable<IRequestContent> paramList, Method method) {
            //HTTPCONENT itself is a abstract class. We can have StringContent, StreamContent,FormURLEncodedContent,MultiPartFormdataContent.
            //Based on the params, we might add the data to content or to the url (in case of get).
            if (paramList == null || paramList?.Count() == 0) return (null, url ?? string.Empty);
            HttpContent processed_content = null;
            string processed_url = url;

            //GET METHODS WITH A BODY: https://stackoverflow.com/questions/978061/http-get-with-request-body
            //A get request can have a content body.

            //The paramlist might containt multiple request param(which will be trasformed in to query). however, only one (the first) request body will be considered
            processed_content = PrepareBody(paramList, method);
            processed_url = PrepareQuery(url, paramList);
            return (processed_content, processed_url);
        }
        HttpContent PrepareBody(IEnumerable<IRequestContent> paramList, Method method) {
            //We can add only one type of body to an object. If we have more than one type, we log the error and take only the first item.
            try {
                HttpContent result = null;
                //paramList.Where(p=> typeof(IRequestBody).IsAssignableFrom(p))?.f
                var _requestBody = paramList.Where(p => p is IRawBodyRequestContent || p is IFormRequestContent || p is IEncodedFormRequestContent)?.FirstOrDefault();
                if (_requestBody == null || _requestBody.Value == null) return result; //Not need of further processing for null values.
                WriteLog(LogLevel.Debug, $@"Request body of type {_requestBody?.GetType()} is getting added to request body.");
                if (_requestBody is IRawBodyRequestContent rawReq) {
                    //Just add a raw content and send.
                    result = PrepareRawBody(rawReq,null);

                } else if (_requestBody is IFormDataRequestContent formreq) {
                    result = PrepareFormBody(formreq);
                } else if (_requestBody is IEncodedFormRequestContent encodedReq) {
                    result = PrepareFormEncodedBody(encodedReq);
                }
                return result;
            } catch (Exception ex) {
                WriteLog(LogLevel.Trace, new EventId(6000), "Error while trying to prepare body", ex);
                return null;
            }
        }
        string PrepareQuery(string url, IEnumerable<IRequestContent> paramList) {
            string result = url;
            //HTTP Parse query automatically encodes/escapedatastring the values. So, if incoming value is already encoded, it is double encoded which results in inconsistencies.
            //var _query = HttpUtility.ParseQueryString(string.Empty);
            StringBuilder query = new StringBuilder();
            var paramQueries = paramList.Where(p => p is IQueryRequestContent)?.Cast<IQueryRequestContent>().ToList(); //Of all the request objects, get only the request queries.
            if (paramQueries == null || paramQueries.Count == 0) return result; //return the input url

            bool startFlag = true;
            foreach (var param in paramQueries) {
                //if something is marked as the decodedoutput, then it should not be further decoded.(Like it should not be URL
                var key = NetUtils.URLSingleEncode(param.Key, param.IsURLDecoded ? param.Key : null);
                var value = param.Value is string valStr ?  NetUtils.URLSingleEncode(valStr, param.IsURLDecoded ? valStr : null) : param.Value;

                if (!startFlag) {
                    query.Append("&");
                }
                query.Append($@"{key}={value}");
                if (startFlag) startFlag = false; //once started start flag is always false.
            }

            var formed_query = query.ToString();
            if (!string.IsNullOrWhiteSpace(formed_query)) {
                result = result + "?" + formed_query;
            }

            //The final formed query will be properly URLSingleEncoded
            return result;
        }
        HttpContent PrepareRawBody(IRawBodyRequestContent rawbody, string key) {
            try {
                //Dont use the Decription of the rawbody request anywhere. It will be used only for internal purpose
                HttpContent result = null;
                if (rawbody.Value == null) return result;
                string mediatype = string.IsNullOrWhiteSpace(rawbody.MIMEType) ? "application/octet-stream" : rawbody.MIMEType;

                //if (rawbody.Value is IEnumerable<object> list && !string.IsNullOrWhiteSpace(key)) {
                //    //Value itself is a string. so, convert it into kvp
                //    var convData = new List<KeyValuePair<string, string>>();
                //    foreach (var item in list) {
                //        convData.Add(new KeyValuePair<string, string>(key, item?.ToString()));
                //    }
                //    return new FormUrlEncodedContent(convData);
                //}

                switch (rawbody.BodyType) {
                    case BodyContentType.StringContent:
                    string _serialized_content = rawbody.Value.ToString(); //Assuming it is already serialized.
                    if (!(rawbody.Value.IsNumericType() || rawbody.Value is string || rawbody.Value is bool || rawbody.Value is Enum) && !rawbody.IsSerialized) {
                        _serialized_content = rawbody.Value.ToJson(_jsonConverters?.Values?.ToList());
                        if (rawbody.OverrideMIMETypeAutomatically) mediatype = "application/json";

                    } else {
                        if (rawbody.OverrideMIMETypeAutomatically) mediatype = "text/plain"; //Because it is a plain string.
                    }

                    if (_reporter != null) {
                        byte[] byteArray = Encoding.UTF8.GetBytes(_serialized_content);
                        MemoryStream stream = new MemoryStream(byteArray);
                        result = new ProgressableStreamContent(stream, _reporter, rawbody.Id) { Title = rawbody.Title, Description = rawbody.Description };
                        result.Headers.ContentDisposition = new ContentDispositionHeaderValue("stream-data") { FileName = rawbody.Title ?? "attachment" };
                    } else {
                        //string content.
                        result = new StringContent(_serialized_content);
                    }
                    break;

                    case BodyContentType.ByteArrayContent:
                    case BodyContentType.StreamContent:
                    if (rawbody.Value is byte[] byteContent) {
                        //If byte content.
                        result = new ByteArrayContent(byteContent, 0, byteContent.Length);
                    } else if (rawbody.Value is Stream streamContent) {
                        if (_reporter != null) {
                            result = new ProgressableStreamContent(streamContent, _reporter, rawbody.Id);
                        } else {
                            //If stream content.
                            result = new StreamContent(streamContent);
                        }
                        //Dont' remove all headers. Only the content type. Header might have authentications properly set.
                        result.Headers.ContentDisposition = new ContentDispositionHeaderValue("stream-data") { FileName = rawbody.Title ?? "attachment" };
                    }
                    break;
                }

                result.Headers.Remove("Content-Type");
                result.Headers.ContentType = new MediaTypeHeaderValue(mediatype);
                return result;
            } catch (Exception ex) {
                WriteLog(LogLevel.Trace, new EventId(6001), "Error while trying to prepare Raw body", ex);
                return null;
            }
        }
        HttpContent PrepareFormBody(IFormDataRequestContent formbody) {
            try {
                //Form can be url encoded form and multi form.. //TODO : REFINE
                //For more than one add as form data.
                MultipartFormDataContent form_content = new MultipartFormDataContent();

                //#####CHECK#####
                //When boundary is added, the upload doesn't work
                //form_content.Headers.Remove("Content-Type");
                //form_content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + _boundary);

                foreach (var item in formbody.Value) {
                    if (item.Value == null || item.Value.Value == null) continue;
                    HttpContent rawContent = null;
                    if (item.Value.Value is HttpContent orgContent) {
                        return orgContent; //Dont' try to do anything else.
                    } else {
                        rawContent = PrepareRawBody(item.Value, item.Key);
                    }
                    if (rawContent == null) continue;
                    if (string.IsNullOrWhiteSpace(item.Value.Title)) {
                        rawContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
                            Name = item.Key
                        };
                    } else {
                        rawContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
                            Name = item.Key,
                            FileName = item.Value.Title
                        };
                    }
                    form_content.Add(rawContent); //Also add the key.
                }
                return form_content;
            } catch (Exception ex) {
                WriteLog(LogLevel.Trace, new EventId(1003), "Error while trying to prepare Form body", ex);
                return null;
            }
        }
        HttpContent PrepareFormEncodedBody(IEncodedFormRequestContent formbody) {
            try {
                return new StringContent(formbody.GetEncodedBodyContent(), null, "application/x-www-form-urlencoded");
            } catch (Exception ex) {
                WriteLog(LogLevel.Trace, new EventId(1004), "Error while trying to prepare Form Encoded body", ex);
                return null;
            }
        }
        #endregion

        #region Common Methods
        public new IRequest SetAuthenticator(IAuthProvider authenticator) {
            base.SetAuthenticator(authenticator);
            return this;
        }

        public new IRequest RemoveAuthenticator() {
            base.RemoveAuthenticator();
            return this;
        }

        public new IRequest SetAuthParam(IAuthParam auth_param) {
            base.SetAuthParam(auth_param);
            return this;
        }

        public new IRequest ResetHeaders() {
            base.ResetHeaders();
            return this;
        }

        public new IRequest ResetHeaders(Dictionary<string, IEnumerable<string>> reset_values) {
            base.ResetHeaders(reset_values);
            return this;
        }

        public new IRequest AddDefaultHeaders() {
            base.AddDefaultHeaders();
            return this;
        }

        public new IRequest AddHeader(string name, string value) {
            base.AddHeader(name, value);
            return this;
        }

        public new IRequest RemoveHeader(string name) {
            base.RemoveHeader(name);
            return this;
        }

        public new IRequest AddHeaderValues(string name, List<string> values) {
            base.AddHeaderValues(name, values);
            return this;
        }
        public new IRequest ReplaceHeader(string name, string value) {
            base.ReplaceHeader(name, value);
            return this;
        }

        public new IRequest ReplaceHeaderValues(string name, List<string> values) {
            base.ReplaceHeaderValues(name, values);
            return this;

        }
        public new IRequest AddJsonConverter(JsonConverter converter) {
            base.AddJsonConverter(converter);
            return this;
        }

        public new IRequest RemoveJsonConverter(JsonConverter converter) {
            base.RemoveJsonConverter(converter);
            return this;
        }

        public new IRequest SetLogger(ILogger logger) {
            base.SetLogger(logger);
            return this;
        }

        public IRequest InheritHeaders(bool inherit) {
            _inherit_headers = inherit;
            return this;
        }

        public IRequest InheritAuthentication(bool inherit_authenticator, bool inherit_parameter) {
            _inherit_authentication = inherit_authenticator;
            _inherit_auth_param = inherit_parameter;
            return this;
        }

        #endregion
        public override string ToString() {
            return this.URL;
        }
    }
}
