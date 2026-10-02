using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OpenSense.Core.Hardware;

/// <summary>
/// Talks to AcerService's local JSON API (127.0.0.1:46933): the same channel PredatorSense's own UI uses to reach
/// AcerLightingService. On 2024+ Predators the keyboard's per-zone RGB never reaches the ACPI-WMI class the rest of
/// OpenSense drives (<c>GetGamingKBBacklight</c> errors, and the actual write happens on the embedded controller's own
/// transport, not the USB bus), so this is the only way found so far to set it without reimplementing that transport.
/// Reached only as a fallback when the keyboard doesn't answer the legacy calls; needs AcerLightingService running.
/// </summary>
public static class AcerServiceTransport
{
    private const int Port = 46933;
    private const uint InitializationPacket = 0;
    private const uint GetUpdatedDataPacket = 20;
    private const uint SetDeviceDataPacket = 100;
    private static readonly byte[] Magic = "ACER"u8.ToArray();

    /// <summary>A short connect attempt; true only when something is actually listening on the port.</summary>
    public static bool IsAvailable(int timeoutMs = 200)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", Port, null, null);
            var connected = result.AsyncWaitHandle.WaitOne(timeoutMs) && client.Connected;
            if (connected)
                client.EndConnect(result);
            return connected;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>GET_UPDATED_DATA for <paramref name="function"/> (e.g. <c>"LIGHTING"</c>); null when it failed.</summary>
    public static JsonNode? Query(string function) => Send(GetUpdatedDataPacket, function, null)?["data"];

    /// <summary>SET_DEVICE_DATA for <paramref name="function"/>; false when it failed or was refused.</summary>
    public static bool SetDeviceData(string function, JsonObject parameters) =>
        Send(SetDeviceDataPacket, function, parameters)?["result"]?.ToString() == "0";

    private static JsonNode? Send(uint packetId, string function, JsonObject? parameters)
    {
        var request = new JsonObject { ["Function"] = function };
        if (parameters is not null)
            request["Parameter"] = parameters;

        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", Port).Wait(500))
                return null;
            client.SendTimeout = 2000;
            client.ReceiveTimeout = 2000;
            var stream = client.GetStream();

            var key = ReadAesKey();
            var body = key is null ? Encoding.UTF8.GetBytes(request.ToJsonString()) : Encrypt(key, request.ToJsonString());
            var packet = new byte[8 + body.Length];
            Magic.CopyTo(packet, 0);
            BitConverter.GetBytes(packetId).CopyTo(packet, 4);
            body.CopyTo(packet, 8);
            stream.Write(packet, 0, packet.Length);

            return ParseResponse(ReadResponse(stream, ms => client.ReceiveTimeout = ms), key);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or AggregateException)
        {
            return null;
        }
    }

    /// <summary>How long to wait for more of a reply once some of it has come: a reply can arrive in several pieces.</summary>
    private const int MoreDataTimeoutMs = 300;

    /// <summary>
    /// A reply as one block of bytes. It is read until the other side has said all of it (nothing more for a moment),
    /// not with a single read: a long reply (the lighting state is hundreds of lines) comes in more than one TCP segment.
    /// </summary>
    /// <param name="setReadTimeout">Sets how long a read waits; used to wait only briefly for the rest once the first piece is in.</param>
    internal static byte[] ReadResponse(Stream stream, Action<int> setReadTimeout)
    {
        var buffer = new byte[65536];
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = stream.Read(buffer, total, buffer.Length - total);
            }
            catch (IOException) when (total > 0)
            {
                break; // the quiet after the last piece
            }
            if (read == 0)
                break;
            total += read;
            setReadTimeout(MoreDataTimeoutMs);
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>The JSON in a reply (after the 8-byte header, decrypted where the channel is keyed); null when it isn't one.</summary>
    internal static JsonNode? ParseResponse(byte[] response, byte[]? key)
    {
        if (response.Length == 0)
            return null;
        var start = response.Length >= 8 && response[0] == 'A' && response[1] == 'C' && response[2] == 'E' && response[3] == 'R' ? 8 : 0;
        try
        {
            var payload = response.AsSpan(start);
            var json = key is null ? Encoding.UTF8.GetString(payload) : Decrypt(key, payload.ToArray());
            return JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or ArgumentException or FormatException)
        {
            return null; // a reply cut short or not what was asked for: the lights are not worth failing the engine over
        }
    }

    /// <summary>Some installs key the channel with an AES-256-ECB key XSense's installer left in the registry.</summary>
    private static byte[]? ReadAesKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Acer\XSense");
        return key?.GetValue("AESkey") as string is { Length: 32 } k ? Encoding.ASCII.GetBytes(k) : null;
    }

    private static byte[] Encrypt(byte[] key, string text)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(text);
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static string Decrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(data, 0, data.Length);
        return Encoding.UTF8.GetString(plain);
    }
}
