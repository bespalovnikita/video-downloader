using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoDownloader.Native;

/// <summary>An immutable, ordered plan of separate output fragments.</summary>
[JsonConverter(typeof(DownloadSectionSetJsonConverter))]
public sealed class DownloadSectionSet : IReadOnlyList<DownloadSection>,IEquatable<DownloadSectionSet>
{
    public const int MaximumCount=32;
    private readonly DownloadSection[] _items;
    public DownloadSectionSet(IEnumerable<DownloadSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var copy=new List<DownloadSection>();
        foreach(var section in sections)
        {
            if(copy.Count==MaximumCount)throw new ArgumentException($"Можно выбрать не более {MaximumCount} фрагментов.");
            if(section is null)throw new ArgumentException("Список фрагментов содержит пустой диапазон.");
            section.Validate();
            copy.Add(new DownloadSection(Math.Round(section.StartSeconds,3),Math.Round(section.EndSeconds,3)));
        }
        if(copy.Count==0)throw new ArgumentException("Добавьте хотя бы один фрагмент.");
        _items=copy.ToArray();Validate();
    }
    public int Count=>_items.Length;
    public DownloadSection this[int index]=>_items[index];
    public IEnumerator<DownloadSection> GetEnumerator()=>((IEnumerable<DownloadSection>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public void Validate(double? duration=null)
    {
        foreach(var section in _items)section.Validate(duration);
        var chronological=_items.OrderBy(section=>section.StartSeconds).ThenBy(section=>section.EndSeconds).ToArray();
        for(int index=1;index<chronological.Length;index++)
            if(chronological[index].StartSeconds<chronological[index-1].EndSeconds)
                throw new ArgumentException("Фрагменты не должны пересекаться или повторяться. Соседние границы допустимы.");
    }
    public bool Equals(DownloadSectionSet? other)=>ReferenceEquals(this,other) || other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? other)=>other is DownloadSectionSet set && Equals(set);
    public override int GetHashCode()
    {
        var hash=new HashCode();foreach(var section in _items)hash.Add(section);return hash.ToHashCode();
    }
}

internal sealed class DownloadSectionSetJsonConverter : JsonConverter<DownloadSectionSet>
{
    public DownloadSectionSetJsonConverter() { }
    public override DownloadSectionSet Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        if(reader.TokenType!=JsonTokenType.StartArray)throw new JsonException("Expected an array of fragments.");
        var sections=new List<DownloadSection>();
        while(reader.Read())
        {
            if(reader.TokenType==JsonTokenType.EndArray)
            {
                try{return new DownloadSectionSet(sections);}
                catch(ArgumentException error){throw new JsonException(error.Message,error);}
            }
            if(sections.Count==DownloadSectionSet.MaximumCount)throw new JsonException("Too many selected fragments.");
            sections.Add(JsonSerializer.Deserialize<DownloadSection>(ref reader,options)??throw new JsonException("Empty fragment."));
        }
        throw new JsonException("Unterminated fragment array.");
    }
    public override void Write(Utf8JsonWriter writer,DownloadSectionSet value,JsonSerializerOptions options)
    {
        writer.WriteStartArray();foreach(var section in value)JsonSerializer.Serialize(writer,section,options);writer.WriteEndArray();
    }
}
