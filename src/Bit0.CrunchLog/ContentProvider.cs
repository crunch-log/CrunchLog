using Bit0.CrunchLog.Config;
using Bit0.CrunchLog.Extensions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bit0.CrunchLog
{
    public class ContentProvider : IContentProvider
    {
        private readonly CrunchConfig _siteConfig;
        private readonly ILogger<ContentProvider> _logger;

        private IDictionary<String, IContent> _allContent;

        public ContentProvider(CrunchConfig siteConfig, ILogger<ContentProvider> logger)
        {
            _logger = logger;
            _siteConfig = siteConfig;
        }

        public IDictionary<String, IContent> AllContent
        {
            get
            {
                if (_allContent != null)
                {
                    return _allContent;
                }

                var allContent = new List<IContent>();
                var files = _siteConfig.Paths.ContentPath.GetFiles("*.md", SearchOption.AllDirectories);


                foreach (var file in files)
                {
                    try
                    {
                        var pipeline = new MarkdownPipelineBuilder()
                            .UseYamlFrontMatter()
                            .Build();
                        var md = Markdown.Parse(file.ReadText(), pipeline);
                        if (md[0] is YamlFrontMatterBlock)
                        {
                            try
                            {
                                var frontMatter = (md[0] as LeafBlock).Lines.ToString();

                                var deserializer = new DeserializerBuilder()
                                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                                    .Build();
                                using (var stringReader = new StringReader(frontMatter))
                                {
                                    var yaml = deserializer.Deserialize(stringReader);

                                    var serializer = new SerializerBuilder()
                                        .JsonCompatible()
                                        .Build();

                                    var json = serializer.Serialize(yaml);
                                    var content = new Content(file, _siteConfig);

                                    JsonConvert.PopulateObject(json, content);
                                    allContent.Add(content);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Error reading front matter from: {file.FullName}");
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Skipping: {file}. Could not find front matter.");
                        }

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error reading front matter from: {file.FullName}");

                    }
                }


                _logger.LogDebug($"Found {allContent.Count} documents");

                _allContent = allContent
                    .OrderByDescending(x => x.DatePublished)
                    .ThenByDescending(x => x.Slug)
                    .ToDictionary(k => k.Id, v => v);

                return _allContent;
            }
        }

        public IEnumerable<IContent> PublishedContent => AllContent.Where(p => p.Value.IsPublished).Select(p => p.Value);
        public IEnumerable<IContent> DraftContent => AllContent.Where(p => !p.Value.IsPublished).Select(p => p.Value);

        public IEnumerable<IContent> Posts => PublishedContent.Where(p => p.Layout == Layouts.Post);

        public IEnumerable<IContent> Pages => PublishedContent.Where(p => p.Layout == Layouts.Page);

        public IEnumerable<IContentListItem> Tags => AllContent.Select(p => p.Value)
                    .Where(p => p.Tags != null && p.Tags.Any())
                    .SelectMany(p => p.Tags)
                    .GroupBy(t => t.Key)
                    .Select(t => t.First().Value)
                    .Select(t => new ContentListItem
                    {
                        Title = $"Tag: {t.Title}",
                        Name = t.Title,
                        Permalink = t.Permalink,
                        Layout = Layouts.Tag,
                        Children = Posts.Where(p => p.Tags.Keys.Contains(t.Title))
                    });

        public IEnumerable<IContentListItem> Categories => AllContent.Select(p => p.Value)
                    .Where(p => p.Categories != null && p.Categories.Any())
                    .SelectMany(p => p.Categories)
                    .GroupBy(c => c.Key)
                    .Select(c => c.First().Value)
                    .Select(c => new ContentListItem
                    {
                        Title = $"Category: {c.Title}",
                        Name = c.Title,
                        Permalink = c.Permalink,
                        Layout = Layouts.Category,
                        Children = Posts.Where(p => p.Categories.Keys.Contains(c.Title))
                    });

        public IEnumerable<IContentListItem> Authors => AllContent.Select(p => p.Value)
                    .Where(p => p.Author != null)
                    .Select(p => p.Author)
                    .Distinct()
                    .Select(a => new ContentListItem
                    {
                        Title = $"Author: {a.Name} ({a.Alias})",
                        Name = a.Name,
                        Permalink = a.Permalink,
                        Layout = Layouts.Author,
                        Children = Posts.Where(p => p.Author.Alias.Equals(a.Alias, StringComparison.InvariantCultureIgnoreCase))
                    });

        public IEnumerable<IContentListItem> PostArchives
        {
            get
            {
                var archives = new List<IContentListItem>();

                var permaLinks = Posts
                    .Select(p => p.Permalink.Split('/'))
                    .ToList();

                var years = permaLinks
                    .Select(x => x[1])
                    .Distinct();

                foreach (var year in years)
                {
                    var ySlug = $"/{year}/";
                    archives.Add(new ContentListItem
                    {
                        Title = $"Archive: {ySlug}",
                        Permalink = ySlug,
                        Layout = Layouts.Archive,
                        Children = Posts.Where(p => p.Permalink.StartsWith(ySlug))
                    });

                    var months = permaLinks
                        .Where(x => x[1] == year)
                        .Select(x => x[2])
                        .Distinct();

                    foreach (var month in months)
                    {
                        var mSlug = $"/{year}/{month}/";
                        archives.Add(new ContentListItem
                        {
                            Title = $"Archive: {mSlug}",
                            Permalink = mSlug,
                            Layout = Layouts.Archive,
                            Children = Posts.Where(p => p.Permalink.StartsWith(mSlug))
                        });
                    }
                }

                return archives.OrderBy(a => a.Permalink);
            }
        }

        public IContentListItem Home => new ContentListItem
        {
            Layout = Layouts.Home,
            Permalink = "/",
            Title = "Home",
            Children = Posts
        };

        public IDictionary<String, IContentBase> Links
        {
            get
            {
                var dict = new Dictionary<String, IContentBase>
                {
                    { "/", Home }
                };

                foreach (var content in PublishedContent)
                {
                    dict.Add(content.Permalink, content);
                }

                foreach (var archive in PostArchives)
                {
                    dict.Add(archive.Permalink, archive);
                }

                foreach (var tags in Tags)
                {
                    dict.Add(tags.Permalink, tags);
                }

                foreach (var category in Categories)
                {
                    dict.Add(category.Permalink, category);
                }

                foreach (var author in Authors)
                {
                    dict.Add(author.Permalink, author);
                }

                return dict.OrderBy(l => l.Key).ToDictionary(k => k.Key, v => v.Value);
            }
        }
    }
}
