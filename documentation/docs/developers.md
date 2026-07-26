This BETHECHAMP build targets the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Once you have it installed:

1. Clone the repository
2. Use `dotnet restore` to restore and install the dependencies.
3. Make your changes
4. Use `dotnet publish` command and you'll get a folder called `bin` in your plugin directory.
5. Navigate to `bin/Release/net10.0/publish/` and copy the contents into `csgo/addons/counterstrikesharp/plugins/MatchZy` (`CounterStrikeSharp.API.dll` and `CounterStrikeSharp.API.pdb` can be skipped).
6. It's done! Now you can test your changes, and also contribute to the plugin if you want to :p 
