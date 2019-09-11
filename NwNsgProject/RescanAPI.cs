using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Blob;

namespace nsgFunc
{
    public static class RescanAPI
    {
        // https://<APP_NAME>.azurewebsites.net/api/rescan/2/17/8
        //
        [FunctionName("RescanAPI")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rescan/APIM/Logs/{blobDay}/{blobHour}/*.json")]
//            [Table("checkpoints", Connection = "AzureWebJobsStorage")] CloudTable checkpointToReset,
            HttpRequest req,
            Binder checkpointsBinder,
            Binder nsgDataBlobBinder,
            string blobDay, string blobHour,
            ILogger log)
        {
            string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
            if (nsgSourceDataAccount.Length == 0)
            {
                log.LogError("Value for nsgSourceDataAccount is required.");
                throw new System.ArgumentNullException("nsgSourceDataAccount", "Please provide setting.");
            }

            string AzureWebJobsStorage = Util.GetEnvironmentVariable("AzureWebJobsStorage");
            if (AzureWebJobsStorage.Length == 0)
            {
                log.LogError("Value for AzureWebJobsStorage is required.");
                throw new System.ArgumentNullException("AzureWebJobsStorage", "Please provide setting.");
            }

            string blobContainerName = Util.GetEnvironmentVariable("blobContainerName");
            if (blobContainerName.Length == 0)
            {
                log.LogError("Value for blobContainerName is required.");
                throw new System.ArgumentNullException("blobContainerName", "Please provide setting.");
            }

            var blobName = $"APIM/Logs/{blobDay}/{blobHour}/*.json";
            var blobDetails = new BlobDetails(blobDay, blobHour);

            var tableAttributes = new Attribute[]
            {
                new TableAttribute("checkpoints"),
                new StorageAccountAttribute("AzureWebJobsStorage")
            };

            try
            {
                CloudTable CheckpointTable = await checkpointsBinder.BindAsync<CloudTable>(tableAttributes);
                TableOperation getOperation = TableOperation.Retrieve<Checkpoint>(blobDetails.GetPartitionKey(), blobDetails.GetRowKey());
                TableResult result = await CheckpointTable.ExecuteAsync(getOperation);
                Checkpoint c = (Checkpoint)result.Result;
                c.CheckpointIndex = 1;
                TableOperation putOperation = TableOperation.InsertOrReplace(c);
                await CheckpointTable.ExecuteAsync(putOperation);
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("Error binding checkpoints table: {0}", ex.Message));
                throw ex;
            }

            var attributes = new Attribute[]
            {
                new BlobAttribute(string.Format("{0}/{1}", blobContainerName, blobName)),
                new StorageAccountAttribute(nsgSourceDataAccount)
            };

            try
            {
                CloudBlockBlob blob = await nsgDataBlobBinder.BindAsync<CloudBlockBlob>(attributes);
                await blob.FetchAttributesAsync();
                var metadata = blob.Metadata;
                if (metadata.ContainsKey("rescan"))
                {
                    int numberRescans = Convert.ToInt32(metadata["rescan"]);
                    metadata["rescan"] = (numberRescans + 1).ToString();
                }
                else
                {
                    metadata.Add("rescan", "1");
                }
                await blob.SetMetadataAsync();
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("Error binding blob input: {0}", ex.Message));
                throw ex;
            }

            return (ActionResult)new OkObjectResult($"NSG flow logs for {blobName} were requested.");

        }
    }
}
