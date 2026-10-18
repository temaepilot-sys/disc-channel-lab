const fs=require('node:fs'),path=require('node:path');
function dotnet(){
  if(process.env.LAB_DOTNET)return process.env.LAB_DOTNET;
  for(let p=path.resolve(__dirname,'../..');;p=path.dirname(p)){
    const candidate=path.join(p,'.dotnet10','sdk','dotnet.exe');
    if(fs.existsSync(candidate))return candidate;
    if(p===path.dirname(p))return 'dotnet';
  }
}
const viewer=()=>path.resolve(process.env.LAB_VIEWER_EXE||'artifacts/personal-win-x64/SpaceSketch/DiscChannelLab.Visualizer.exe');
module.exports={dotnet,viewer};
