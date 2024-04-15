using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Firehose.Antelope.Sharp.Types;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcAntelopeFirehoseClient;
using Sf.Firehose.V2;

namespace Firehose.Antelope.Sharp.FirehoseClient;

public class FirehoseBatchClient
{
    private readonly Sf.Firehose.V2.Stream.StreamClient? _streamClient;

    private AsyncServerStreamingCall<Response>? _streamingCall;

    private readonly Channel<Any> _serializedBlocksChannel =
        Channel.CreateUnbounded<Any>(
            new UnboundedChannelOptions() { SingleReader = false, SingleWriter = false });

    private readonly ChannelWriter<Any> _serializedBlocksChannelWriter;

    private readonly CancellationTokenSource _cancellationTokenSource;

    private Task _readerTask;

    private ulong _batchSize;

    private int _receivedBlocksCount;

    private ulong _unpackedBlocksCount;

    private readonly FirehoseClientOptions _options;

    public FirehoseBatchClient(FirehoseClientOptions options, bool disableTls = false)
    {
        GrpcChannel? firehoseChannel;
        _options = options;

        if (disableTls)
        {
            var httpClientHandler = new HttpClientHandler();
            httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            var httpClient = new HttpClient(httpClientHandler);
            firehoseChannel = GrpcChannel.ForAddress(_options.BaseAddress, new GrpcChannelOptions() { MaxReceiveMessageSize = int.MaxValue, HttpClient = httpClient });
        }
        else
            firehoseChannel = GrpcChannel.ForAddress(_options.BaseAddress, new GrpcChannelOptions() { MaxReceiveMessageSize = int.MaxValue });

        _streamClient = new Sf.Firehose.V2.Stream.StreamClient(firehoseChannel);
        _serializedBlocksChannelWriter = _serializedBlocksChannel.Writer;
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public FirehoseBatchClient(FirehoseClientOptions options, GrpcChannel firehoseChannel)
    {
        _options = options;
        _streamClient = new Sf.Firehose.V2.Stream.StreamClient(firehoseChannel);
        _serializedBlocksChannelWriter = _serializedBlocksChannel.Writer;
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async Task StartAsync(long startBlockNum, ulong batchSize, string? bearer = null)
    {
        try
        {
            _streamEnded = false;

            _batchSize = batchSize;

            _unpackedBlocksCount = 0;
            _receivedBlocksCount = 0;

            bearer ??= (await GetBearer())?.token;

            if (bearer == null)
                return;

            _streamingCall?.Dispose();
            _streamingCall = _streamClient.Blocks(
                new Request()
                {
                    StartBlockNum = startBlockNum,
                    StopBlockNum = (ulong)(startBlockNum + (long)_batchSize),
                },
                new Metadata()
                {
                    new("Authorization", $"Bearer {bearer}"),
                },
                DateTime.MaxValue,
                CancellationToken.None
            );

            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(10 * 60000); // we cancel after 10 minutes

            while (await _streamingCall.ResponseStream.MoveNext(cancellationTokenSource.Token))
            {
                if (_streamingCall.ResponseStream.Current != null)
                {
                    _receivedBlocksCount++;
                    await _serializedBlocksChannelWriter.WriteAsync(_streamingCall.ResponseStream.Current.Block);
                }

                cancellationTokenSource.Dispose();
                cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(10 * 60000);
            }

            if (cancellationTokenSource.IsCancellationRequested && _receivedBlocksCount != (int)batchSize)
            {
                _readerTask = StartAsync(startBlockNum + _receivedBlocksCount, batchSize - (ulong)_receivedBlocksCount);
                return;
            }

            _streamEnded = true;
        }
        catch (System.Exception e)
        {
            _readerTask = StartAsync(startBlockNum + _receivedBlocksCount, batchSize - (ulong)_receivedBlocksCount);
        }
    }

    public void Start(long startBlockNum, ulong batchSize)
    {
        _readerTask = StartAsync(startBlockNum, batchSize - 1);
    }

    public async Task StopAsync()
    {
        await _cancellationTokenSource.CancelAsync();

        while (!_readerTask.IsCanceled)
        {
            await Task.Delay(1000);
        }
    }

    public async Task<Block> ReadAsync(CancellationToken cancellationToken)
    {
        var reader = _serializedBlocksChannel.Reader;
        var serializedBlock = await reader.ReadAsync(cancellationToken);
        var deserializedBlock = serializedBlock.Unpack<Block>();
        _unpackedBlocksCount++;

        return deserializedBlock;
    }

    private bool _streamEnded;

    public bool HasFinished => _streamEnded && _serializedBlocksChannel.Reader.Count == 0;

    private async Task<BearerResponse?> GetBearer()
    {
        using (var client = new HttpClient())
        {
            client.BaseAddress = new Uri(_options.AuthBaseAddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var jsonData = JsonSerializer.Serialize(new { api_key = _options.PinaxApiKey });
            var contentData = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/v1/auth/issue", contentData);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<BearerResponse>(await response.Content.ReadAsStringAsync())!;
            }
        }

        return null;
    }
}