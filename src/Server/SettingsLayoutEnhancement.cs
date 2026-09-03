namespace VirtualMonitorsUniverse.Server;

internal static class SettingsLayoutEnhancement
{
    public const string Content = """
<style>
.settingsPage fieldset.generalSettings .formgrid{grid-template-columns:max-content minmax(0,1fr)}.settingsPage fieldset.generalSettings .formgrid>label{white-space:nowrap}.hotkeyCapture{width:210px;cursor:pointer}.hotkeyCapture.capturing{outline:2px solid #5b8def}.hotkeyHelp{font-size:12px;color:#667085;margin-top:5px;max-width:560px}
</style>
<script>
(() => {
  const legend=[...document.querySelectorAll('.settingsPage fieldset legend')].find(x=>x.textContent.trim()==='Web and Logging');
  if(!legend)return;
  legend.textContent='General';
  const fieldset=legend.closest('fieldset');
  fieldset?.classList.add('generalSettings');
  const grid=fieldset?.querySelector('.formgrid');
  if(!grid)return;

  const label=document.createElement('label');
  label.htmlFor='fullscreenHotkey';
  label.textContent='Exit Fullscreen Hotkey';
  const cell=document.createElement('div');
  const input=document.createElement('input');
  input.id='fullscreenHotkey';
  input.className='hotkeyCapture mediumInput';
  input.type='text';
  input.readOnly=true;
  input.spellcheck=false;
  input.autocomplete='off';
  input.value='Win+Alt+F11';
  input.title='Click and press a key combination.';
  const help=document.createElement('div');
  help.className='hotkeyHelp';
  help.textContent='Reserved by the VMU Terminal while the VMU page has keyboard focus. Plain F11 remains available to applications inside Terminal.';
  cell.append(input,help);
  grid.append(label,cell);

  let savedHotkey=input.value;
  let pendingHotkey=input.value;
  let capturing=false;

  function keyName(event){
    const key=event.key;
    if(['Control','Shift','Alt','Meta'].includes(key))return null;
    if(/^F([1-9]|1[0-9]|2[0-4])$/.test(key))return key;
    if(key.length===1&&/[a-z0-9]/i.test(key))return key.toUpperCase();
    const names={Escape:'Esc',Enter:'Enter',Space:'Space',Tab:'Tab',Home:'Home',End:'End',PageUp:'PageUp',PageDown:'PageDown',ArrowUp:'Up',ArrowDown:'Down',ArrowLeft:'Left',ArrowRight:'Right',Insert:'Insert',Delete:'Delete'};
    return names[key]||null;
  }
  function formatHotkey(event){
    const key=keyName(event);if(!key)return null;
    const parts=[];
    if(event.metaKey)parts.push('Win');
    if(event.ctrlKey)parts.push('Ctrl');
    if(event.altKey)parts.push('Alt');
    if(event.shiftKey)parts.push('Shift');
    if(parts.length===0)return null;
    parts.push(key);
    return parts.join('+');
  }
  function beginCapture(){capturing=true;input.classList.add('capturing');input.value='Press shortcut…';}
  function endCapture(value){capturing=false;input.classList.remove('capturing');input.value=value;}
  input.addEventListener('click',beginCapture);
  input.addEventListener('focus',()=>{if(!capturing)beginCapture();});
  input.addEventListener('blur',()=>{if(capturing)endCapture(pendingHotkey);});
  input.addEventListener('keydown',event=>{
    if(!capturing)return;
    event.preventDefault();event.stopPropagation();
    if(event.key==='Escape'){endCapture(pendingHotkey);return;}
    const value=formatHotkey(event);if(!value)return;
    pendingHotkey=value;endCapture(value);
  });

  const previousFetch=window.fetch.bind(window);
  window.fetch=async(inputArg,init)=>{
    try{
      const url=typeof inputArg==='string'?inputArg:inputArg?.url;
      const path=url?new URL(url,location.href).pathname:'';
      if(path==='/api/settings'&&String(init?.method||'GET').toUpperCase()==='POST'&&typeof init?.body==='string'){
        const payload=JSON.parse(init.body);
        payload.hotkeys={fullscreenExit:pendingHotkey||'Win+Alt+F11'};
        init={...init,body:JSON.stringify(payload)};
      }
    }catch{}
    return previousFetch(inputArg,init);
  };

  previousFetch('/api/settings',{cache:'no-store'}).then(r=>r.ok?r.json():null).then(settings=>{
    const value=settings?.hotkeys?.fullscreenExit||'Win+Alt+F11';
    savedHotkey=value;pendingHotkey=value;endCapture(value);
  }).catch(()=>{});

  document.getElementById('reset')?.addEventListener('click',()=>{pendingHotkey=savedHotkey;endCapture(savedHotkey);});
})();
</script>
""";
}
