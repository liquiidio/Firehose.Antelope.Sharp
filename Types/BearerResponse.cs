namespace Firehose.Antelope.Sharp.Types;

public class BearerResponse
{
    public string token { get; set; }
    public int expires_at { get; set; }
}