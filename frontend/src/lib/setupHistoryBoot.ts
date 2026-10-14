/** Static, pre-hydration dispatcher. Inert unless Setup has an active guard.
 * Window-target listeners run in registration order, including capture listeners;
 * registration before Next is necessary to preserve the actual unsaved editor.
 */
export const SETUP_HISTORY_BOOT = `(function(){
  if(window.__kynexSetupHistoryInstalled)return;
  window.__kynexSetupHistoryInstalled=true;
  window.addEventListener('popstate',function(event){
    if(!window.dispatchEvent(new CustomEvent('kynexone:setup-history',{cancelable:true,detail:event})))event.stopImmediatePropagation();
  });
})();`;
