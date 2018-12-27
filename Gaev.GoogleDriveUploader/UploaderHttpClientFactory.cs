using System;
using Google.Apis.Http;

namespace Gaev.GoogleDriveUploader
{
    public class UploaderHttpClientFactory : IHttpClientFactory
    {
        readonly HttpClientFactory _factory = new HttpClientFactory();

        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var cli = _factory.CreateHttpClient(args);
            cli.Timeout = TimeSpan.FromMinutes(20);
            return cli;
        }
    }
}