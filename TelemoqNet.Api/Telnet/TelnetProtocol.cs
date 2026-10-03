namespace TelemoqNet.Api.Telnet;

public static class TelnetProtocol
{
    public const byte IAC = 255;

    public const byte DONT = 254;
    public const byte DO = 253;
    public const byte WONT = 252;
    public const byte WILL = 251;

    public const byte SB = 250;
    public const byte SE = 240;

    public const byte AYT = 246;
    public const byte IP = 244;
    public const byte BRK = 243;

    public const byte ECHO = 1;
    public const byte SUPPRESS_GO_AHEAD = 3;
    public const byte NAWS = 31;
}