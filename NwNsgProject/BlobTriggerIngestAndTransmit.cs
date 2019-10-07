using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Buffers;

namespace nsgFunc
{
    public static class BlobTriggerIngestAndTransmit
    {
        [FunctionName("BlobTriggerIngestAndTransmit")]
        public static async Task Run(
            [BlobTrigger("%blobContainerName%/APIM/Logs/{date}/{hr}/{name}", Connection = "%nsgSourceDataAccount%")]CloudBlockBlob myBlob,
            [Table("checkpoints", Connection = "AzureWebJobsStorage")] CloudTable checkpointTable,
            Binder nsgDataBlobBinder,
            Binder cefLogBinder,
            string date, string hr, 
            string name,
            ExecutionContext executionContext,
            ILogger log)
        {
            log.LogInformation($"BlobTriggerIngestAndTransmit triggered: {executionContext.InvocationId} ");

            string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
            if (nsgSourceDataAccount.Length == 0)
            {
                log.LogError("Value for nsgSourceDataAccount is required.");
                throw new System.ArgumentNullException("nsgSourceDataAccount", "Please provide setting.");
            }

            string blobContainerName = Util.GetEnvironmentVariable("blobContainerName");
            if (blobContainerName.Length == 0)
            {
                log.LogError("Value for blobContainerName is required.");
                throw new System.ArgumentNullException("blobContainerName", "Please provide setting.");
            }

            string outputBinding = Util.GetEnvironmentVariable("outputBinding");
            if (outputBinding.Length == 0)
            {
                log.LogError("Value for outputBinding is required. Permitted values are: 'arcsight', 'splunk', 'eventhub'.");
                throw new System.ArgumentNullException("outputBinding", "Please provide setting.");
            }

            var blobDetails = new BlobDetails(date, hr, name, blobContainerName);

            // get checkpoint
            Checkpoint checkpoint = Checkpoint.GetCheckpoint(blobDetails, checkpointTable, log);
            log.LogInformation($"Fetched check point details. Checkpoint index:{checkpoint.CheckpointIndex} ");

            var blockList = myBlob.DownloadBlockListAsync().Result;
            var startingByte = blockList.Where((item, index) => index < checkpoint.CheckpointIndex).Sum(item => item.Length);
            var endingByte = blockList.Where((item, index) => index < blockList.Count()).Sum(item => item.Length);
            var dataLength = endingByte - startingByte;

            log.LogInformation("Blob: {0}, starting byte: {1}, ending byte: {2}, number of bytes: {3}, no of blocks: {4}", blobDetails.ToString(), startingByte, endingByte, dataLength, blockList.Count());

            if (dataLength == 0)
            {
                log.LogWarning(string.Format("Blob: {0}, triggered on completed hour.", blobDetails.ToString()));
                return;
            }
            //foreach (var item in blockList)
            //{
            //    log.LogInformation("Name: {0}, Length: {1}", item.Name, item.Length);
            //}

            var attributes = new Attribute[]
            {
                new BlobAttribute(string.Format("{0}/{1}", blobContainerName, myBlob.Name)),
                new StorageAccountAttribute(nsgSourceDataAccount)
            };

            string nsgMessagesString = "";
            var bytePool = ArrayPool<byte>.Shared;
            byte[] nsgMessages = bytePool.Rent((int)dataLength);
            try
            {                
                CloudBlockBlob blob = nsgDataBlobBinder.BindAsync<CloudBlockBlob>(attributes).Result;
                await blob.DownloadRangeToByteArrayAsync(nsgMessages, 0, startingByte, dataLength);
                
                
                nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages, 0, (int)dataLength);
                // if (nsgMessages[0] == ',')
                // {
                //     nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages, 1, (int)(dataLength - 1));
                // } else
                // {
                //     nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages, 0, (int)dataLength);
                // }
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("Error binding blob input: {0}", ex.Message));
                throw ex;
            }
            finally
            {
                bytePool.Return(nsgMessages);
            }

            //log.LogDebug(nsgMessagesString);
            

            try
            {
                int bytesSent = await Util.SendMessagesDownstreamAsync(nsgMessagesString, executionContext, cefLogBinder, log);
                log.LogInformation($"Sending {nsgMessagesString.Length} bytes (denormalized to {bytesSent} bytes) downstream via output binding {outputBinding}.");
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("SendMessagesDownstreamAsync: Error {0}", ex.Message));
                throw ex;
            }

            checkpoint.PutCheckpoint(checkpointTable, blockList.Count());
        }
    }
}
