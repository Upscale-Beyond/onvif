using System.Text.Json.Serialization;
namespace Upscale.Connectors.Onvif.Models;

public class PtzPosition
{
    [JsonPropertyName("pan")]
    public double Pan { get; set; }

    [JsonPropertyName("tilt")]
    public double Tilt { get; set; }

    [JsonPropertyName("zoom")]
    public double Zoom { get; set; }
}