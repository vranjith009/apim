using System;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Extensions.Logging;


namespace nsgFunc
{
    public class Checkpoint : TableEntity
    {
        public int CheckpointIndex { get; set; }  // index of the last processed block list item

        public Checkpoint()
        {
        }

        public Checkpoint(string partitionKey, string rowKey, string blockName, long offset, int index)
        {
            PartitionKey = partitionKey;
            RowKey = rowKey;
            CheckpointIndex = index;
        }

        public static Checkpoint GetCheckpoint(BlobDetails blobDetails, CloudTable checkpointTable, ILogger log)
        {
            TableOperation operation = TableOperation.Retrieve<Checkpoint>(
                blobDetails.GetPartitionKey_1(), blobDetails.GetRowKey_1());
            TableResult result = checkpointTable.ExecuteAsync(operation).Result;

            Checkpoint checkpoint = (Checkpoint)result.Result;
            if (checkpoint == null)
            {
                log.LogInformation($"New file, no entry in checkpoint table.  File Name:{blobDetails.FileName}.");

                checkpoint = new Checkpoint(blobDetails.GetPartitionKey_1(), blobDetails.GetRowKey_1(), "", 0, 1);
            }
            if (checkpoint.CheckpointIndex == 0)
            {
                checkpoint.CheckpointIndex = 1;
            }

            return checkpoint;
        }

        public void PutCheckpoint(CloudTable checkpointTable, int index)
        {
            CheckpointIndex = index;

            TableOperation operation = TableOperation.InsertOrReplace(this);
            checkpointTable.ExecuteAsync(operation).Wait();
        }
    }
}