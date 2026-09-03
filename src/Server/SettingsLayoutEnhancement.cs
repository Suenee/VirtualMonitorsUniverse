namespace VirtualMonitorsUniverse.Server;

internal static class SettingsLayoutEnhancement
{
    public const string Content = """
<style>
.settingsPage fieldset.generalSettings .formgrid{grid-template-columns:max-content minmax(0,1fr)}.settingsPage fieldset.generalSettings .formgrid>label{white-space:nowrap}
</style>
<script>
(() => {
  const legend=[...document.querySelectorAll('.settingsPage fieldset legend')].find(x=>x.textContent.trim()==='Web and Logging');
  if(!legend)return;legend.textContent='General';legend.closest('fieldset')?.classList.add('generalSettings');
})();
</script>
""";
}
