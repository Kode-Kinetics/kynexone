/** Static, document-lifetime position bookkeeping; cancellation is Setup-only.
 * Window-target listeners run in registration order, including capture listeners;
 * registration before Next is necessary to preserve the actual unsaved editor.
 */
export const SETUP_HISTORY_BOOT = `(function(){
  if(window.__kynexSetupHistoryInstalled)return;
  window.__kynexSetupHistoryInstalled=true;
  var history=window.history,key='__kynexSetupPosition';
  var position=typeof history.state?.[key]==='number'?history.state[key]:0;
  var push=history.pushState,replace=history.replaceState;
  function stamp(state,next){return Object.assign({},state,{[key]:next});}
  function written(){window.dispatchEvent(new CustomEvent('kynexone:history-write',{detail:position}));}
  replace.call(history,stamp(history.state,position),'');
  history.pushState=function(state,unused,url){
    var next=position+1;
    push.call(history,stamp(state,next),unused,url);
    position=next;written();
  };
  history.replaceState=function(state,unused,url){
    replace.call(history,stamp(state,position),unused,url);written();
  };
  window.addEventListener('popstate',function(event){
    if(typeof event.state?.[key]==='number')position=event.state[key];
    if(!window.dispatchEvent(new CustomEvent('kynexone:setup-history',{cancelable:true,detail:event})))event.stopImmediatePropagation();
  });
})();`;
