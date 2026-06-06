namespace Apps2Samsung.Helpers.Jellyfin.Plugins.EditorsChoice
{
    public class PatchEditorsChoice
    {
        public string ApplyAsync(string js)
        {
            string css = @"
.hss-hero { 
    position: relative; 
    width: 94%; 
    height: 500px !important; 
    margin: 30px auto 30px auto !important; 
    overflow: hidden; 
    border-radius: 15px; 
    background: #000; 
    border: 0.06em solid var(--borderColor) !important;
}

.hss-bg { 
    position: absolute; 
    inset: 0; 
    width: 100%; 
    height: 100%; 
    object-fit: cover; 
    opacity: 0; 
    transition: opacity 1s; 
    z-index: 1; 
}

.hss-overlay { 
    position: absolute; 
    inset: 0; 
    background: linear-gradient(
        90deg, 
        rgba(0, 0, 0, 1) 0%, 
        rgba(0, 0, 0, 0) 60%, 
        rgba(0, 0, 0, 0) 100%
    ) !important; 
    z-index: 2; 
}

.hss-content { 
    position: absolute; 
    inset: 0; 
    padding: 30px !important; 
    display: flex; 
    flex-direction: column; 
    justify-content: center; 
    z-index: 3; 
    pointer-events: none; 
}

.hss-logo { 
    max-width: 400px; 
    max-height: 120px; 
    object-fit: contain; 
    margin-bottom: 15px; 
    pointer-events: auto; 
}

.hss-logo + div, 
.hss-rate {
    color: #fff !important;
    font-size: 1em !important;
    font-weight: 400 !important;
    height: 10px !important;
}

.hss-overview { 
    color: #eee !important; 
    width: 45%; 
    font-size: 0.9em !important; 
    line-height: 1.5; 
    margin-bottom: 25px !important; 
    display: -webkit-box; 
    -webkit-line-clamp: 4; 
    -webkit-box-orient: vertical; 
    overflow: hidden; 
    text-overflow: ellipsis !important;
    pointer-events: auto; 
}

.hss-btn { 
    background: #fff !important; 
    color: #000 !important; 
    border: none; 
    padding: 0.9em 1em !important; 
    border-radius: 0.5em !important; 
    font-weight: 500 !important; 
    font-size: 1em !important; 
    text-transform: none !important; 
    cursor: pointer; 
    pointer-events: auto; 
    width: fit-content; 
    box-shadow: none !important;
}

.hss-btn:focus { 
    background: var(--highlightOutlineColor) !important; 
    color: #fff !important; 
    transform: none !important; 
    outline: none; 
}

.editorsChoiceItemBanner, 
.editorsChoiceItemsContainer { 
    display: none !important; 
}
";


            return @"
(function () {
    var style = document.createElement('style');
    style.innerText = `" + css.Replace("\r\n", " ").Replace("\"", "\\\"") + @"`;
    document.head.appendChild(style);

    function init() {
        var api = window.ApiClient || (window.ConnectionManager && window.ConnectionManager.getcurrentitem().apiClient);
        var container = document.querySelector('.sections') || document.querySelector('.homeSectionsContainer');
        
        if (!api || !container || document.getElementById('hss-hero')) return;

        api.getItems(api.getCurrentUserId(), { IncludeItemTypes: 'Movie', SortBy: 'Random', SortOrder: 'Descending', Limit: 8, Recursive: true, Fields: 'Overview,ImageTags,CommunityRating' }).then(function(res) {
            var items = res.Items || [];
            if (!items.length) return;

            var html = `
                <div id=""hss-hero"" class=""hss-hero"">
                    <img class=""hss-bg"" crossorigin=""anonymous"">
                    <div class=""hss-overlay""></div>
                    <div class=""hss-content"">
                        <img class=""hss-logo"" crossorigin=""anonymous"">
                        <div style=""color:#f5c518; font-size:1.6em; font-weight:bold; margin-bottom:10px;"">⭐ <span class=""hss-rate""></span></div>
                        <p class=""hss-overview""></p>
                        <button class=""hss-btn"">Watch Now</button>
                    </div>
                </div>`;
            container.insertAdjacentHTML('afterbegin', html);

            var idx = 0;
            function slide() {
                var item = items[idx];
                var el = document.getElementById('hss-hero');
                var host = api.serverAddress();
                var token = api.accessToken();

                var bg = el.querySelector('.hss-bg');
                bg.style.opacity = '0';
                
                setTimeout(function() {
                    bg.src = host + '/Items/' + item.Id + '/Images/Backdrop/0?api_key=' + token;
                    bg.onload = function() { bg.style.opacity = '0.8'; };
                    
                    var logo = el.querySelector('.hss-logo');
                    if (item.ImageTags.Logo) {
                        logo.src = host + '/Items/' + item.Id + '/Images/Logo/0?api_key=' + token;
                        logo.style.display = 'block';
                    } else { logo.style.display = 'none'; }

                    el.querySelector('.hss-rate').innerText = (item.CommunityRating || '7.5');
                    el.querySelector('.hss-overview').innerText = item.Overview || '';
                    el.querySelector('.hss-btn').onclick = function() { 
                        if(window.Emby && window.Emby.Page) window.Emby.Page.showItem(item.Id);
                        else window.location.hash = '#!/item?id=' + item.Id;
                    };
                    idx = (idx + 1) % items.length;
                }, 400);
            }
            slide();
            setInterval(slide, 10000);
        });
    }
    setInterval(init, 2000);
})();";
        }
    }
}
