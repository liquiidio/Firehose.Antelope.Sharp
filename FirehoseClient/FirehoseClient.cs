using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Firehose.Antelope.Sharp.Types;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcAntelopeFirehoseClient;
using Sf.Firehose.V2;

namespace Firehose.Antelope.Sharp.FirehoseClient;

public class FirehoseClient : IDisposable
{
    private readonly string _baseAddress;
    private readonly string _authBaseAddress;
    private readonly string _pinaxApiKey;

    private BearerResponse _bearer;
    private readonly GrpcChannel _firehoseChannel;
    private readonly Sf.Firehose.V2.Stream.StreamClient _streamClient;
    private AsyncServerStreamingCall<Response> _streamingCall;

    private readonly Channel<Google.Protobuf.WellKnownTypes.Any> _serializedBlocksChannel =
        Channel.CreateUnbounded<Google.Protobuf.WellKnownTypes.Any>(
            new UnboundedChannelOptions() { SingleReader = false, SingleWriter = false });

    private ChannelWriter<Google.Protobuf.WellKnownTypes.Any> _serializedBlocksChannelWriter;

    public ChannelReader<Google.Protobuf.WellKnownTypes.Any> _serializedBlocksChannelReader;

    public FirehoseClient(string pinaxApiKey, string baseAddress = "https://wax.firehose.pinax.network:443", string authBaseAddress = "https://auth.pinax.network")
    {
        _pinaxApiKey = pinaxApiKey;
        _baseAddress = baseAddress;
        _authBaseAddress = authBaseAddress;

        _serializedBlocksChannelWriter = _serializedBlocksChannel.Writer;
        _serializedBlocksChannelReader = _serializedBlocksChannel.Reader;
    }

    public async Task ReadAsync(long startBlockNum, ulong stopBlockNum)
    {
        BearerResponse bearer = await GetBearer();

        GrpcChannel firehoseChannel = GrpcChannel.ForAddress(_baseAddress, new GrpcChannelOptions());
        Sf.Firehose.V2.Stream.StreamClient streamClient = new Sf.Firehose.V2.Stream.StreamClient(firehoseChannel);

        AsyncServerStreamingCall<Response> streamingCall = streamClient.Blocks(
            new Request()
            {
                StartBlockNum = startBlockNum,
                StopBlockNum = stopBlockNum,
            },
            new Metadata()
            {
                new("Authorization", $"Bearer {bearer.token}"),
            },
            DateTime.MaxValue,
            CancellationToken.None
        );

        while (await streamingCall.ResponseStream.MoveNext(CancellationToken.None))
        {
            if (streamingCall.ResponseStream.Current != null)
            {
                await _serializedBlocksChannelWriter.WriteAsync(streamingCall.ResponseStream.Current.Block);
            }
        }
    }

    private async Task<BearerResponse> GetBearer()
    {
        using (var client = new HttpClient())
        {
            client.BaseAddress = new Uri(_authBaseAddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var jsonData = JsonSerializer.Serialize(new { api_key = _pinaxApiKey });
            var contentData = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/v1/auth/issue", contentData);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<BearerResponse>(await response.Content.ReadAsStringAsync())!;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _firehoseChannel?.Dispose();
    }
}