namespace VirtualMonitorsUniverse.Server;

internal static class SettingsLayoutEnhancement
{
    public const string Content = """
<style>
.settingsPage fieldset.generalSettings .formgrid{grid-template-columns:max-content minmax(0,1fr)}.settingsPage fieldset.generalSettings .formgrid>label{white-space:nowrap}.hotkeyCapture{width:210px;cursor:pointer}.hotkeyCapture.capturing{outline:2px solid #5b8def}.hotkeyCapture:disabled{cursor:not-allowed;opacity:.55}.hotkeyHelp{font-size:12px;color:#667085;margin-top:5px;max-width:620px}.terminalInputDisabled{opacity:.55}
</style>
<script>
(() => {
  const form=document.getElementById('form');
  const save=document.getElementById('save');
  const reset=document.getElementById('reset');
  const legend=[...document.querySelectorAll('.settingsPage fieldset legend')].find(x=>x.textContent.trim()==='Web and Logging');
  if(!form||!save||!legend)return;
  legend.textContent='General';
  const fieldset=legend.closest('fieldset');
  fieldset?.classList.add('generalSettings');
  const grid=fieldset?.querySelector('.formgrid');
  if(!grid)return;

  const hotkeyLabel=document.createElement('label');
  hotkeyLabel.htmlFor='terminalF11Hotkey';
  hotkeyLabel.textContent='Send F11 to Terminal Hotkey';
  const hotkeyCell=document.createElement('div');
  const hotkeyInput=document.createElement('input');
  hotkeyInput.id='terminalF11Hotkey';
  hotkeyInput.className='hotkeyCapture mediumInput';
  hotkeyInput.type='text';
  hotkeyInput.readOnly=true;
  hotkeyInput.spellcheck=false;
  hotkeyInput.autocomplete='off';
  hotkeyInput.value='Win+Alt+F11';
  hotkeyInput.title='Click and press a key combination.';
  const hotkeyHelp=document.createElement('div');
  hotkeyHelp.className='hotkeyHelp';
  hotkeyCell.append(hotkeyInput,hotkeyHelp);
  grid.append(hotkeyLabel,hotkeyCell);

  const mouseLabel=document.createElement('label');
  mouseLabel.htmlFor='mousePassthroughImmediately';
  mouseLabel.textContent='Mouse Passthrough Immediately';
  const mouseCell=document.createElement('div');
  mouseCell.className='checkboxCell';
  const mouseImmediate=document.createElement('input');
  mouseImmediate.id='mousePassthroughImmediately';
  mouseImmediate.type='checkbox';
  mouseImmediate.title='When enabled, entering the active Terminal image immediately enters the virtual monitor while this browser window has focus.';
  mouseCell.append(mouseImmediate);
  grid.append(mouseLabel,mouseCell);

  let savedSettings=null;
  let savedHotkey='Win+Alt+F11';
  let pendingHotkey=savedHotkey;
  let savedMouseImmediate=false;
  let capturing=false;
  let baseline='';
  let keyboardAvailable=false;
  let mouseAvailable=false;

  function q(id){return document.getElementById(id);}
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
  function beginCapture(){if(hotkeyInput.disabled)return;capturing=true;hotkeyInput.classList.add('capturing');hotkeyInput.value='Press shortcut…';}
  function endCapture(value){capturing=false;hotkeyInput.classList.remove('capturing');hotkeyInput.value=value;updateDirty();}
  hotkeyInput.addEventListener('click',beginCapture);
  hotkeyInput.addEventListener('focus',()=>{if(!capturing)beginCapture();});
  hotkeyInput.addEventListener('blur',()=>{if(capturing)endCapture(pendingHotkey);});
  hotkeyInput.addEventListener('keydown',event=>{
    if(!capturing)return;
    event.preventDefault();event.stopPropagation();
    if(event.key==='Escape'){endCapture(pendingHotkey);return;}
    const value=formatHotkey(event);if(!value)return;
    pendingHotkey=value;endCapture(value);
  });

  function applyAvailability(){
    hotkeyInput.disabled=!keyboardAvailable;
    hotkeyLabel.classList.toggle('terminalInputDisabled',!keyboardAvailable);
    hotkeyHelp.textContent=keyboardAvailable
      ?'While an active VMU Terminal has keyboard focus, this shortcut sends F11 to the virtual monitor. Plain F11 remains available to the browser.'
      :'Enable Keyboard passthrough on at least one monitor to configure this shortcut.';
    mouseImmediate.disabled=!mouseAvailable;
    mouseLabel.classList.toggle('terminalInputDisabled',!mouseAvailable);
  }

  function state(){
    return JSON.stringify({
      vmuInterface:q('vmuInterface')?.value||'',vmuPort:q('vmuPort')?.value||'',
      webInterface:q('webInterface')?.value||'',webPort:q('webPort')?.value||'',
      socketInterface:q('socketInterface')?.value||'',socketPort:q('socketPort')?.value||'',
      retention:q('retention')?.value||'',preview:q('preview')?.value||'',
      snap:q('snapTolerance')?.value||'',startup:q('startupEnabled')?.checked===true,
      exit:q('exit')?.value||'',restore:q('restore')?.checked===true,
      terminalF11:pendingHotkey,mouseImmediate:mouseImmediate.checked
    });
  }
  function updateDirty(){if(!baseline){save.disabled=true;return;}save.disabled=state()===baseline;}
  function establishBaseline(){baseline=state();save.disabled=true;}

  function applySettings(settings){
    if(!settings)return;
    const set=(id,value)=>{const el=q(id);if(el&&value!==undefined&&value!==null)el.value=value;};
    set('vmuInterface',settings.vmu?.interface);set('vmuPort',settings.vmu?.port);
    set('webInterface',settings.web?.interface);set('webPort',settings.web?.port);
    set('socketInterface',settings.socket?.interface);set('socketPort',settings.socket?.port);
    set('retention',Math.ceil((settings.logging?.retentionMinutes??10080)/1440));
    set('preview',settings.webUi?.monitorPreviewRefreshSeconds??60);
    set('snapTolerance',Math.min(50,Math.max(5,settings.webUi?.arrangementSnapTolerancePx??15)));
    const startup=q('startupEnabled');if(startup)startup.checked=settings.startup?.enabled===true;
    set('exit',settings.exit?.monitorAction==='Keep'?'Keep':'Disconnect');
    const restore=q('restore');if(restore)restore.checked=settings.exit?.restoreServices===true;
    savedHotkey=settings.hotkeys?.terminalF11Forward||settings.hotkeys?.fullscreenExit||'Win+Alt+F11';
    pendingHotkey=savedHotkey;hotkeyInput.value=savedHotkey;
    savedMouseImmediate=settings.terminalInput?.mousePassthroughImmediately===true;
    mouseImmediate.checked=savedMouseImmediate;
    q('vmuInterface')?.dispatchEvent(new Event('change'));
  }

  form.addEventListener('input',updateDirty);
  form.addEventListener('change',()=>setTimeout(updateDirty,0));
  save.disabled=true;

  const previousFetch=window.fetch.bind(window);
  window.fetch=async(inputArg,init)=>{
    try{
      const url=typeof inputArg==='string'?inputArg:inputArg?.url;
      const path=url?new URL(url,location.href).pathname:'';
      if(path==='/api/settings'&&String(init?.method||'GET').toUpperCase()==='POST'&&typeof init?.body==='string'){
        const payload=JSON.parse(init.body);
        payload.hotkeys={terminalF11Forward:pendingHotkey||'Win+Alt+F11'};
        payload.terminalInput={mousePassthroughImmediately:mouseImmediate.checked===true};
        init={...init,body:JSON.stringify(payload)};
      }
    }catch{}
    return previousFetch(inputArg,init);
  };

  Promise.all([
    previousFetch('/api/settings',{cache:'no-store'}).then(r=>r.ok?r.json():null),
    previousFetch('/api/monitors',{cache:'no-store'}).then(r=>r.ok?r.json():[])
  ]).then(([settings,monitors])=>{
    savedSettings=settings;
    keyboardAvailable=Array.isArray(monitors)&&monitors.some(m=>m?.configuration?.collaborationKeyboard===true);
    mouseAvailable=Array.isArray(monitors)&&monitors.some(m=>m?.configuration?.collaborationMouse===true);
    applyAvailability();
    setTimeout(()=>{applySettings(settings);establishBaseline();},60);
  }).catch(()=>{applyAvailability();setTimeout(establishBaseline,60);});

  reset?.addEventListener('click',()=>{
    setTimeout(()=>{
      if(savedSettings)applySettings(savedSettings);
      pendingHotkey=savedHotkey;hotkeyInput.value=savedHotkey;
      mouseImmediate.checked=savedMouseImmediate;
      establishBaseline();
    },0);
  });
})();
</script>
""";
}
