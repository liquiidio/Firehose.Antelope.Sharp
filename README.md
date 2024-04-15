<div align="center">
 <img src="https://avatars.githubusercontent.com/u/82725791?s=200&v=4" align="center"
     alt="Liquiid logo" width="280" height="300">
</div>

# Firehose.Antelope.Sharp

### Usage

#### Configuration

In order to interact with Firehose Grpc-Server you can either use the FirehoseClient-Class directly or start multiple streams by using the FirehoseBatchClient.

The FirehoseBatchClient allows for a little more felxibility when it comes to parallelism and allows to stream/receive Blocks in parallel using multiple Clients.

#####  Example FirehoseClient:

```csharp
	using Firehose.Antelope.Sharp.FirehoseClient;
	using GrpcAntelopeFirehoseClient;


	// Instantiate new instance of the FirehoseClient
	using var client = new FirehoseClient("Your-Pinax-Api-Key-here");

	// start streaming a Range of Blocks
	_ = client.ReadAsync(startBlockNum: 0, stopBlockNum: 100);

	// iterate received Blocks
	await foreach (var serializedBlock in client._serializedBlocksChannelReader.ReadAllAsync())
	{
		var unpackedBlock = serializedBlock.Unpack<Block>();
	}
```

##### Example FirehoseBatchClient:

```csharp
var batchClients = new ConcurrentQueue<FirehoseBatchClient>();
ulong batchSize = 5000;
var parallelClientsCount = 5;
ulong lastRequestedBlockNum = 0;

var clientOptions = new FirehoseClientOptions() { PinaxApiKey = "Your-Pinax-Api-Key-here" };

// start parallel Clients
for (int i = 0; i < parallelClientsCount; i++)
{
    var reader = new FirehoseBatchClient(clientOptions);
    reader.Start((long)(lastRequestedBlockNum + 1), batchSize);
    lastRequestedBlockNum += batchSize;
    batchClients.Enqueue(reader);
}

// set current Client to first Client n Queue (lowest blockNum)
batchClients.TryDequeue(out var currentClient);

while (true)
{
    // if current Client has finished and number of active Clients is smaller than
    // max parallel Clients start new Client
    if (currentClient.HasFinished)
    {
        if (batchClients.Count < parallelClientsCount)
        {
            for (int i = 0; i < parallelClientsCount - batchClients.Count; i++)
            {
                var newClient = new FirehoseBatchClient(clientOptions);
                newClient.Start((long)(lastRequestedBlockNum + 1), batchSize);
                lastRequestedBlockNum += batchSize;
                batchClients.Enqueue(newClient);
            }
        }

        // Try dequeue the next Client
        var dequeued = batchClients.TryDequeue(out currentClient);
        while (!dequeued)
        {
            dequeued = batchClients.TryDequeue(out currentClient);
        }
    }

    // Read deserialized Block
    var block = await currentClient.ReadAsync(CancellationToken.None);
}
```
