using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Haley.Models {
    public class RestResponse<T> : BaseResponse where T : class {
        public T Content { get; private set; }
        string _stringcontent;
        Func<string, T> _converter;

        public RestResponse<T> SetContent(T content) {
            Content = content;
            return this;
        }
        async Task<Stream> GetDecodedStream() {
            try {
                var possible_encoding = OriginalContent.Headers.ContentEncoding.FirstOrDefault(); //assuming there is only one encoding possible.
                var originalStream = await OriginalContent.ReadAsStreamAsync();
                Stream result = originalStream;
                result.Position = 0; //Because if we try to read multiple times, it will not read the values.
                if (possible_encoding.ToLower().Contains("gzip")) {
                    result = new GZipStream(originalStream, CompressionMode.Decompress);
                } else if (possible_encoding.ToLower().Contains("deflate")) {
                    result = new DeflateStream(originalStream, CompressionMode.Decompress);
                }
                //else if (possible_encoding.ToLower().Contains("br")) {
                //    result = new GZipStream(originalStream, CompressionMode.Decompress);
                //}
                return result;
            } catch (Exception ex) {
                throw;
            }
        }
        public async Task<RestResponse<T>> FetchContent() {
            try {
                if (base.OriginalContent == null) return this;

                Stream decoded_stream = null;
                //If content is encoded, we need to first decode it.
                if (IsContentEncoded) {
                    decoded_stream = await GetDecodedStream();
                }
                if (typeof(T) == typeof(byte[])) {
                    Content = (await base.OriginalContent?.ReadAsByteArrayAsync()) as T;
                } else if (typeof(T) == typeof(string)) {
                    if (IsContentEncoded) {
                        //Should we also use different encoding?
                        var sreader = new StreamReader(decoded_stream, Encoding.Default); //Using block will dispose the stream.
                        Content = (await sreader.ReadToEndAsync()) as T;
                        //decoded_stream.Position = 0; //reset at the beginning.
                        //using (var sreader = new StreamReader(decoded_stream, Encoding.Default)) {
                        //    Content = await sreader.ReadToEndAsync() as T;
                        //}
                    } else {
                        Content = await base.OriginalContent?.ReadAsStringAsync() as T;
                    }
                } else if (typeof(T) == typeof(Stream)) {
                    if (IsContentEncoded) {
                        Content = decoded_stream as T;
                    } else {
                        Content = (await base.OriginalContent?.ReadAsStreamAsync()) as T;
                    }
                } else {
                    //Get it as string and then try to convert it to the object using converters of some sort.
                    _stringcontent = await base.OriginalContent?.ReadAsStringAsync();
                    if (_converter != null) {
                        Content = _converter.Invoke(_stringcontent);
                    }
                }
                return this;
            } catch (Exception ex) {
                throw;
            }
        }

        public RestResponse<T> SetConveter(Func<string, T> converter) {
            _converter = converter;
            return this;
        }

        public RestResponse(HttpResponseMessage response) : base(response) { }
    }
}
