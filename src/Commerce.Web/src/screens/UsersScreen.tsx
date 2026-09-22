import { useEffect, useState, type FormEvent } from 'react'
import { listUsers, createUser, updateUserRoles, adminResetPassword } from '@/api/account'
import type { UserSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const assignableRoles = ['business-admin', 'seller', 'provider']
export function UsersScreen() {
 const [users, setUsers] = useState<UserSummary[]>([]); const [email,setEmail]=useState(''); const [password,setPassword]=useState(''); const [roles,setRoles]=useState<string[]>(['seller']); const [resetPasswords,setResetPasswords]=useState<Record<string,string>>({}); const [error,setError]=useState<string | null>(null)
 const refresh=async()=>{ try { setUsers(await listUsers()) } catch { setError('Unable to load users.') } }; useEffect(()=>{ void refresh() },[])
 const submit=async(e:FormEvent)=>{e.preventDefault(); try {await createUser({email,password,roleNames:roles,branchIds:[]});setEmail('');setPassword('');await refresh()}catch{setError('Unable to create user.')}}
 const toggle=(role:string)=>setRoles(current=>current.includes(role)?current.filter(x=>x!==role):[...current,role])
 const forceReset=async(userId:string)=>{const newPassword=resetPasswords[userId]?.trim();if(!newPassword){setError('Enter a replacement password.');return}try{await adminResetPassword(userId,{newPassword});setResetPasswords(current=>({...current,[userId]:''}))}catch{setError('Unable to reset password.')}}
 return <section><h2>Users</h2>{error&&<p role="alert">{error}</p>}<form onSubmit={submit}><Input aria-label="User email" value={email} onChange={e=>setEmail(e.target.value)} required/><Input aria-label="User password" type="password" value={password} onChange={e=>setPassword(e.target.value)} required/>{assignableRoles.map(role=><label key={role}><input type="checkbox" checked={roles.includes(role)} onChange={()=>toggle(role)}/>{role}</label>)}<Button type="submit">Create user</Button></form><ul>{users.map(user=><li key={user.userId}>{user.email} <Button onClick={()=>void updateUserRoles(user.userId,roles)}>Save roles</Button><Input aria-label={`Replacement password for ${user.email}`} type="password" value={resetPasswords[user.userId]??''} onChange={e=>setResetPasswords(current=>({...current,[user.userId]:e.target.value}))}/><Button onClick={()=>void forceReset(user.userId)}>Force reset</Button></li>)}</ul></section>
}
