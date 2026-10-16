import { employeeRolesApi } from '@/api/employees'
import { MasterDataScreen } from './MasterDataScreen'

/** Staff positions ABM ("puestos": carnicero, cajero, repartidor...; `/employees/roles`); every employee may have one. */
export function EmployeeRolesScreen() {
  return (
    <MasterDataScreen
      namespace="employeeRoles"
      api={employeeRolesApi}
      viewKey="employeeRoles"
      nameInUseCode="employee-role-name-in-use"
      keyInUseCode="employee-role-key-in-use"
    />
  )
}
