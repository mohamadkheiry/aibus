export const normalizeGlobalSearch = (value:string) => value
  .toLocaleLowerCase('fa')
  .replace(/[يى]/g,'ی')
  .replace(/ك/g,'ک')
  .replace(/[\u200c\u200e\u200f]/g,' ')
  .replace(/\s+/g,' ')
  .trim()

export const globalSearchMatches = (searchable:string, query:string) => {
  const normalizedQuery=normalizeGlobalSearch(query)
  if(!normalizedQuery)return true
  const normalizedSearchable=normalizeGlobalSearch(searchable)
  return normalizedQuery.split(' ').every(part=>normalizedSearchable.includes(part))
}

export const rankGlobalSearch = <T extends {searchable:string}>(items:T[],query:string,limit=6) => {
  const normalizedQuery=normalizeGlobalSearch(query)
  if(!normalizedQuery)return items.slice(0,limit)
  return items
    .filter(item=>globalSearchMatches(item.searchable,normalizedQuery))
    .map((item,index)=>{
      const searchable=normalizeGlobalSearch(item.searchable)
      const score=searchable===normalizedQuery?0:searchable.startsWith(normalizedQuery)?1:2
      return {item,index,score}
    })
    .sort((a,b)=>a.score-b.score||a.index-b.index)
    .slice(0,limit)
    .map(result=>result.item)
}
