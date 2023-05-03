﻿namespace dotnet_isolated_azurefunction.OuputBinding
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Extensions.Dapr;

    public class MultipleOutputBindings
    {
        [Function("MultiOutput")]
        public static MyOutputType Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req,
        FunctionContext context)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.WriteString("Success!");

            string myQueueOutput = "some output";

            return new MyOutputType()
            {
                Name = myQueueOutput,
                HttpResponse = response
            };
        }
    }

    public class MyOutputType
    {
        [DaprStateOutput("%StateStoreName%", Key = "product")]
        public string? Name { get; set; }

        public HttpResponseData? HttpResponse { get; set; }
    }
}
