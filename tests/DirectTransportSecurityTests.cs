using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GoingCooperative.Core;

internal static class DirectTransportSecurityTests
{
    public static int Run()
    {
        var failures = 0;
        var code = DirectTransportSecurity.GenerateSessionCode();
        var key = new byte[0];
        if (code.Length != 32 || !DirectTransportSecurity.TryDeriveKey(code, out key, out _))
        {
            Console.Error.WriteLine("FAIL direct security generated code");
            failures++;
        }
        if (DirectTransportSecurity.TryDeriveKey("1234", out _, out _))
        {
            Console.Error.WriteLine("FAIL direct security rejects weak code");
            failures++;
        }

        var payload = Encoding.UTF8.GetBytes("authenticated save and control data");
        var wire = new MemoryStream();
        var sender = new AuthenticatedRecordStream(wire, key, "C2H", "H2C");
        sender.Write(payload, 0, payload.Length);
        var encoded = wire.ToArray();
        wire.Position = 0;
        var receiver = new AuthenticatedRecordStream(wire, key, "H2C", "C2H");
        var received = new byte[payload.Length];
        var read = receiver.Read(received, 0, received.Length);
        if (read != payload.Length || Encoding.UTF8.GetString(received) != Encoding.UTF8.GetString(payload))
        {
            Console.Error.WriteLine("FAIL authenticated record roundtrip");
            failures++;
        }

        encoded[16] ^= 0x01;
        try
        {
            var tampered = new AuthenticatedRecordStream(new MemoryStream(encoded), key, "H2C", "C2H");
            tampered.Read(received, 0, received.Length);
            Console.Error.WriteLine("FAIL authenticated record tamper rejection");
            failures++;
        }
        catch (InvalidDataException)
        {
        }

        failures += TestTcpHandshake(key);

        if (failures == 0) Console.WriteLine("PASS DirectTransportSecurity");
        return failures;
    }

    private static int TestTcpHandshake(byte[] key)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception hostFailure = null;
        var host = new Thread(() =>
        {
            try
            {
                using (var socket = listener.AcceptTcpClient())
                {
                    var stream = DirectTransportSecurity.AuthenticateTcpHost(socket.GetStream(), key);
                    var request = DirectTransportSecurity.ReadExact(stream, 4);
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex) { hostFailure = ex; }
        });
        host.Start();
        try
        {
            using (var socket = new TcpClient())
            {
                socket.Connect(IPAddress.Loopback, port);
                var stream = DirectTransportSecurity.AuthenticateTcpClient(socket.GetStream(), key);
                var value = new byte[] { 1, 2, 3, 4 };
                stream.Write(value, 0, value.Length);
                stream.Flush();
                var response = DirectTransportSecurity.ReadExact(stream, 4);
                if (!DirectTransportSecurity.FixedTimeEquals(value, response)) throw new InvalidDataException("TCP protected echo mismatch.");
            }
            host.Join(5000);
            if (host.IsAlive || hostFailure != null) throw hostFailure ?? new TimeoutException("TCP protected host timed out.");
            listener.Stop();
            return 0;
        }
        catch (Exception ex)
        {
            listener.Stop();
            Console.Error.WriteLine("FAIL authenticated TCP handshake " + ex.GetType().Name + ":" + ex.Message);
            return 1;
        }
    }

}
