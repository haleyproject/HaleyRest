using Haley.Abstractions;
using Haley.Models;
using Haley.Rest;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Haley.Abstractions;

namespace Haley.Utils {
    public static class ResponseExtensions {
        public static async Task<StringResponse> AsStringResponseAsync(this IResponse response) {
            if (response is StringResponse) return response as StringResponse;
            return new StringResponse(response.OriginalResponse);
        }

        public static async Task<StreamResponse> AsStreamReponseAsync(this IResponse response) {
            if (response is StreamResponse) return response as StreamResponse;
            return new StreamResponse(response.OriginalResponse); //Stream is already read
        }

        //public static async Task<ByteArrayResponse> AsByteArrayResponseAsync(this IResponse response) {
        //    if (response is ByteArrayResponse) return response as ByteArrayResponse;
        //    var result = new ByteArrayResponse(response.OriginalResponse);
        //    return await result.FetchContent() as ByteArrayResponse;
        //}
    }
}
