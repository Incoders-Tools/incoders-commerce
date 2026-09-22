import { useEffect, useState, type FormEvent } from 'react'
import { createBranch, listBranches } from '@/api/account'
import type { BranchSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
export function BranchesScreen() { const [branches,setBranches]=useState<BranchSummary[]>([]);const [name,setName]=useState('');const [error,setError]=useState<string|null>(null);const refresh=async()=>{try{setBranches(await listBranches())}catch{setError('Unable to load branches.')}};useEffect(()=>{void refresh()},[]);const submit=async(e:FormEvent)=>{e.preventDefault();try{await createBranch({branchName:name});setName('');await refresh()}catch{setError('Unable to create branch.')}};return <section><h2>Branches</h2>{error&&<p role="alert">{error}</p>}<form onSubmit={submit}><Input aria-label="Branch name" value={name} onChange={e=>setName(e.target.value)} required/><Button type="submit">Create branch</Button></form><ul>{branches.map(branch=><li key={branch.branchId}>{branch.branchName}</li>)}</ul></section> }
