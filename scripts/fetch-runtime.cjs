const fs = require('node:fs');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const version = '8.0.32';
const destination = 'tools/downloads/nuget';
fs.mkdirSync(destination, { recursive: true });
Promise.all(['microsoft.netcore.app.host.win-x64', 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64'].map(async name => {
    if (fs.existsSync(`${destination}/${name}.${version}.nupkg`)) {
        return;
    }
    const response = await fetch(`https://api.nuget.org/v3-flatcontainer/${name}/${version}/${name}.${version}.nupkg`);
    if (!response.ok) {
        throw new Error(`${name}: ${response.status}`);
    }
    await pipeline(Readable.fromWeb(response.body), fs.createWriteStream(`${destination}/${name}.${version}.nupkg`));
    console.log(`Downloaded ${name} ${version}`);
})).catch(error => {
    console.error(error);
    process.exitCode = 1;
});
