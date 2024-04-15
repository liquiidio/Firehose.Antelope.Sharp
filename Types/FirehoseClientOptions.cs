namespace Firehose.Antelope.Sharp.Types
{
    public class FirehoseClientOptions
    { 
        public string PinaxApiKey { get; set; }
        public string BaseAddress { get; set; } = "https://wax.firehose.pinax.network:443";
        public string AuthBaseAddress { get; set; } = "https://auth.pinax.network";
    }
}
