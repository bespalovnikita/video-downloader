using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

internal sealed record RemoteDownloadConnection
{
    internal Uri Address { get; }
    internal string Username { get; }
    internal string Password { get; }

    internal RemoteDownloadConnection(Uri address,string username,string password)
    {
        Address=NormalizeAddress(address);
        if(string.IsNullOrWhiteSpace(username) || username.Contains(':') || username.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || password.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(username)+Encoding.UTF8.GetByteCount(password)+1>3072)
            throw new ArgumentException(ErrorMessages.Text("Проверьте логин и пароль сервера.","Check the server username and password."));

        Username=username;
        Password=password;
    }

    internal static Uri ParseAddress(string address)
    {
        if(address.Any(char.IsControl) || !Uri.TryCreate(address.Trim(),UriKind.Absolute,out var parsed))throw InvalidAddress();
        return NormalizeAddress(parsed);
    }

    private static Uri NormalizeAddress(Uri address)
    {
        if(!address.IsAbsoluteUri || address.Host.Length==0 || address.UserInfo.Length!=0 || address.Query.Length!=0 ||
            address.Fragment.Length!=0 || address.AbsoluteUri.Length>2048 ||
            (address.Scheme!=Uri.UriSchemeHttps && (address.Scheme!=Uri.UriSchemeHttp || !address.IsLoopback)))
            throw InvalidAddress();
        return address.AbsoluteUri.EndsWith('/')?address:new Uri(address.AbsoluteUri+"/",UriKind.Absolute);
    }

    private static ArgumentException InvalidAddress()=>new(ErrorMessages.Text(
        "Укажите HTTPS-адрес сервера или локальный HTTP-адрес SSH-туннеля, без логина, параметров и фрагмента в URL.",
        "Enter the server's HTTPS address or a loopback HTTP address for the SSH tunnel. Keep credentials, query parameters and fragments out of the URL."));
    public override string ToString()=>nameof(RemoteDownloadConnection);
}

internal sealed record RemoteDownloadSettings(bool Enabled,RemoteDownloadConnection? Connection);

internal sealed class RemoteDownloadSettingsStore(string directory)
{
    private const int MaxBytes=64*1024;
    private readonly string _path=Path.Combine(directory,"remote-downloads.json");

    private sealed record ProtectedSettings(int Version,string Payload);

    private sealed class ConnectionSettings
    {
        public bool Enabled { get; init; }
        public string? Address { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
    }

    internal RemoteDownloadSettings Read()
    {
        try
        {
            using var file=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(file.Length>MaxBytes)throw new InvalidDataException("Remote download settings exceed the allowed size.");
            var contents=new byte[checked((int)file.Length)];
            file.ReadExactly(contents);
            if(file.ReadByte()!=-1)throw new InvalidDataException("Remote download settings changed while reading.");
            var stored=JsonSerializer.Deserialize<ProtectedSettings>(contents,AppPersistence.Json);
            if(stored is not {Version:1} || string.IsNullOrEmpty(stored.Payload))
                throw new InvalidDataException("Invalid remote download settings format.");
            byte[] payload=ProtectedData.Unprotect(Convert.FromBase64String(stored.Payload),null,DataProtectionScope.CurrentUser);
            try
            {
                var settings=JsonSerializer.Deserialize<ConnectionSettings>(payload,AppPersistence.Json);
                if(settings is null)throw new InvalidDataException("Missing remote download settings.");
                if(!settings.Enabled && settings is {Address:null,Username:null,Password:null})return new(false,null);
                if(settings.Address is null || settings.Username is null || settings.Password is null)
                    throw new InvalidDataException("Incomplete remote download connection settings.");
                return new(settings.Enabled,new RemoteDownloadConnection(RemoteDownloadConnection.ParseAddress(settings.Address),
                    settings.Username,settings.Password));
            }
            finally{CryptographicOperations.ZeroMemory(payload);}
        }
        catch(FileNotFoundException){return new(false,null);}
    }

    internal void Save(RemoteDownloadSettings settings)
    {
        if(settings.Enabled && settings.Connection is null)throw new ArgumentException("An enabled server requires credentials.");
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(new ConnectionSettings
        {
            Enabled=settings.Enabled,
            Address=settings.Connection?.Address.AbsoluteUri,
            Username=settings.Connection?.Username,
            Password=settings.Connection?.Password
        },AppPersistence.Json);
        try
        {
            // Seal the endpoint together with its credentials. An edited file
            // cannot move a protected password to a different server address.
            string encoded=Convert.ToBase64String(ProtectedData.Protect(payload,null,DataProtectionScope.CurrentUser));
            AppPersistence.AtomicWrite(_path,JsonSerializer.Serialize(new ProtectedSettings(1,encoded),AppPersistence.Json));
        }
        finally{CryptographicOperations.ZeroMemory(payload);}
    }
}
