using System;

namespace NuvioTV.Core.Networking
{
    public class NetworkResult<T>
    {
        public string Status { get; set; }
        public T Data { get; set; }
        public string Message { get; set; }
        public string Code { get; set; }

        public static NetworkResult<T> Loading()
        {
            return new NetworkResult<T> { Status = "loading" };
        }

        public static NetworkResult<T> Success(T data)
        {
            return new NetworkResult<T> { Status = "success", Data = data };
        }

        public static NetworkResult<T> Error(string message, string code = null)
        {
            return new NetworkResult<T>
            {
                Status = "error",
                Message = message ?? "Unknown error",
                Code = code
            };
        }
    }
}